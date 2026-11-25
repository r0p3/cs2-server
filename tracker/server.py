"""Serve the tracker and exchange state with the CS2 plugin."""
import hashlib
import hmac
import json
import os
import re
import tempfile
import time
import uuid
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
STATE = Path(os.environ.get("REPSERVER_TRACKER_STATE", "/var/lib/repserver/tracker"))
SESSION = re.compile(r"Bearer ([0-9]{17})\.([0-9a-f]{48})")
STATIC = {"/": ("index.html", "text/html; charset=utf-8"),
          "/app.js": ("app.js", "text/javascript; charset=utf-8"),
          "/style.css": ("style.css", "text/css; charset=utf-8")}
ACTIONS = {
    "live": None, "pause": None, "unpause": None, "restart": None, "kick_bot": None,
    "16t": None, "16ct": None, "map": r"[a-z0-9_]{1,64}",
    "mode": r"bot-game|bot-game-t|bot-dm|bot-dm-pistol|win|16|max",
    "set_bot_t": r"[1-9][0-9]?", "set_bot_ct": r"[1-9][0-9]?",
}


def read_json(path, default=None):
    try:
        with path.open(encoding="utf-8") as file:
            return json.load(file)
    except FileNotFoundError:
        return default


def write_json(path, data):
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as file:
        temporary = Path(file.name)
        try:
            json.dump(data, file, ensure_ascii=False)
            file.flush()
            os.fsync(file.fileno())
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


def tracker_status():
    live = read_json(STATE / "live.json", {})
    if live.get("online"):
        updated = datetime.fromisoformat(live["updatedAt"])
        live["online"] = (datetime.now(timezone.utc) - updated).total_seconds() < 10
    return {"live": live, "history": read_json(STATE / "history.json", {})}


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        self.timeout = 12
        super().setup()

    def send_data(self, code, data, content_type):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'")
        self.end_headers()
        self.wfile.write(data)

    def respond(self, code, data):
        self.send_data(code, json.dumps(data, ensure_ascii=False).encode("utf-8"),
                       "application/json; charset=utf-8")

    def session(self):
        match = SESSION.fullmatch(self.headers.get("Authorization", ""))
        if match is None:
            return None
        steam_id, token = match.groups()
        session = read_json(STATE / "sessions" / (steam_id + ".json"))
        digest = hashlib.sha256(token.encode("ascii")).hexdigest()
        if session is None or session.get("expires", 0) <= time.time() or not hmac.compare_digest(
                session.get("tokenHash", ""), digest):
            return None
        try:
            admins = (STATE.parent / "admins.txt").read_text(encoding="utf-8").splitlines()
        except (FileNotFoundError, PermissionError):
            admins = []
        return steam_id, session["name"], digest, any(line.strip() == steam_id for line in admins)

    def do_GET(self):
        try:
            if self.path in STATIC:
                filename, content_type = STATIC[self.path]
                self.send_data(200, (ROOT / filename).read_bytes(), content_type)
            elif self.path == "/api/tracker":
                self.respond(200, tracker_status())
            elif self.path == "/api/profile":
                session = self.session()
                if session is None:
                    self.respond(401, {"error": "Open a fresh link from !trackeradmin in CS2."})
                    return
                _, name, _, admin = session
                self.respond(200, {"name": name, "admin": admin})
            else:
                self.respond(404, {"error": "Page not found."})
        except (OSError, ValueError) as error:
            self.log_error("Tracker read failed: %s", error)
            self.respond(503, {"error": "Tracker data is temporarily unavailable."})

    def read_body(self):
        if self.headers.get_content_type() != "application/json":
            raise ValueError("Expected a JSON request.")
        length = int(self.headers.get("Content-Length", 0))
        if not 0 < length <= 4096:
            raise ValueError("Invalid request size.")
        return json.loads(self.rfile.read(length))

    def do_POST(self):
        try:
            if self.path != "/api/admin":
                self.respond(404, {"error": "Page not found."})
                return
            session = self.session()
            if session is None:
                self.respond(401, {"error": "Open a fresh link from !trackeradmin in CS2."})
                return
            steam_id, _, digest, admin = session
            if admin:
                self.admin_action(steam_id, digest, self.read_body())
            else:
                self.respond(403, {"error": "This Steam account does not have admin access."})
        except ValueError:
            self.respond(400, {"error": "Invalid request."})
        except OSError as error:
            self.log_error("Tracker write failed: %s", error)
            self.respond(503, {"error": "The tracker could not save the request."})

    def admin_action(self, steam_id, digest, body):
        command = body.get("command") if isinstance(body, dict) else None
        argument = body.get("argument") if isinstance(body, dict) else None
        if not isinstance(command, str) or command not in ACTIONS:
            raise ValueError("Unknown action.")
        pattern = ACTIONS[command]
        if (pattern is None and argument is not None) or (
                pattern is not None and (not isinstance(argument, str) or not re.fullmatch(pattern, argument))):
            raise ValueError("Invalid argument.")
        if not tracker_status()["live"].get("online"):
            self.respond(503, {"error": "The game server is offline."})
            return
        request_id = uuid.uuid4().hex + ".json"
        request = STATE / "requests" / request_id
        response = STATE / "responses" / request_id
        write_json(request, {"player": steam_id, "tokenHash": digest, "command": command,
                             "argument": argument, "expires": int(time.time()) + 5})
        deadline = time.monotonic() + 6
        try:
            while time.monotonic() < deadline:
                result = read_json(response)
                if result is not None:
                    self.respond(200, {"message": result})
                    return
                time.sleep(0.15)
            self.respond(504, {"error": "No reply from the server. Check match status before retrying."})
        finally:
            request.unlink(missing_ok=True)
            response.unlink(missing_ok=True)


if __name__ == "__main__":
    with ThreadingHTTPServer((os.environ.get("TRACKER_HOST", "0.0.0.0"), 8080), Handler) as server:
        server.serve_forever()
