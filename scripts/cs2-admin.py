#!/usr/bin/env python3
"""Desktop admin panel for RepServer through a local SSH forward."""
import queue
import re
import socket
import struct
import threading
import time
import tkinter as tk
from tkinter import messagebox, scrolledtext, ttk
from urllib.parse import parse_qs, urlsplit


RESPONSE_VALUE = 0
EXEC_COMMAND = AUTH_RESPONSE = 2
AUTH = 3
TIMEOUT = 5
MAX_REPLY = 4 * 1024 * 1024
MAP_NAME = re.compile(r"[a-z0-9_]{1,64}")
WORKSHOP_PENDING = "[RepServer] Workshop lookup pending."


def packet(request_id, kind, body=b""):
    return struct.pack("<iii", len(body) + 10, request_id, kind) + body + b"\0\0"


def read_exact(sock, size, deadline):
    data = bytearray()
    while len(data) < size:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("RCON response timed out.")
        sock.settimeout(remaining)
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("The server closed the RCON connection.")
        data.extend(chunk)
    return bytes(data)


def read_packet(sock, deadline):
    size, = struct.unpack("<i", read_exact(sock, 4, deadline))
    if not 10 <= size <= MAX_REPLY:
        raise ValueError("Invalid RCON packet size.")
    frame = read_exact(sock, size, deadline)
    if not frame.endswith(b"\0\0"):
        raise ValueError("Invalid RCON packet terminator.")
    request_id, kind = struct.unpack_from("<ii", frame)
    return request_id, kind, frame[8:-2]


def rcon(port, password, command):
    encoded = command.encode("utf-8")
    if not encoded or len(encoded) > 4086 or any(c in encoded for c in b"\0\r\n"):
        raise ValueError("Enter a single console command of at most 4,086 bytes.")
    with socket.create_connection(("127.0.0.1", port), timeout=TIMEOUT) as sock:
        sock.sendall(packet(1, AUTH, password.encode("ascii")))
        deadline = time.monotonic() + TIMEOUT
        while True:
            request_id, kind, _ = read_packet(sock, deadline)
            if kind == AUTH_RESPONSE and request_id == -1:
                raise PermissionError("RCON rejected the password.")
            if kind == AUTH_RESPONSE and request_id == 1:
                break

        # CS2 may attach command output to the terminator request ID.
        try:
            sock.sendall(packet(2, EXEC_COMMAND, encoded) + packet(3, RESPONSE_VALUE))
            deadline = time.monotonic() + TIMEOUT
            output = bytearray()
            while True:
                request_id, kind, body = read_packet(sock, deadline)
                if kind != RESPONSE_VALUE or request_id not in (2, 3):
                    continue
                if request_id == 3 and not body:
                    return output.decode("utf-8", errors="replace").strip()
                output.extend(body)
                if len(output) > MAX_REPLY:
                    raise ValueError("RCON output exceeded the display limit.")
        except OSError as exc:
            detail = str(exc) or "RCON response timed out."
            raise ConnectionError(
                f"{detail} Check server status before retrying. The command may have run."
            ) from exc


def map_choices(response):
    choices = []
    for line in response.splitlines():
        if line.startswith("[RepServer] "):
            aliases = line.removeprefix("[RepServer] ").split(", ")
            if all(MAP_NAME.fullmatch(alias) for alias in aliases):
                choices.extend(aliases)
    return choices


def workshop_id(text):
    if not re.fullmatch(r"[0-9]+", text):
        url = urlsplit(text if "://" in text else "https://" + text)
        if (
            url.scheme not in ("https", "http")
            or url.hostname not in ("steamcommunity.com", "www.steamcommunity.com")
            or url.path.rstrip("/") not in ("/sharedfiles/filedetails", "/workshop/filedetails")
        ):
            raise ValueError("Enter a Steam Workshop map link or numeric ID.")
        ids = parse_qs(url.query, keep_blank_values=True).get("id", [])
        text = ids[0] if len(ids) == 1 else ""
    if not re.fullmatch(r"[0-9]{1,20}", text) or not 0 < int(text) < 2**64:
        raise ValueError("Enter a valid Workshop ID.")
    return str(int(text))


class AdminPanel:
    def __init__(self, root):
        self.root = root
        self.results = queue.Queue()
        self.busy = False
        self.port = tk.StringVar(value="27016")
        self.password = tk.StringVar()
        self.map_name = tk.StringVar()
        self.mode = tk.StringVar(value="bot-game")
        self.workshop = tk.StringVar()
        self.alias = tk.StringVar()
        self.team = tk.StringVar(value="T")
        self.bot_count = tk.StringVar(value="5")
        self.user_id = tk.StringVar()
        self.announcement = tk.StringVar()
        self.console = tk.StringVar()
        self.status = tk.StringVar(value="Open the SSH tunnel, then select Check connection.")

        root.title("RepServer Admin")
        root.geometry("820x600")
        root.minsize(710, 490)
        surface = ttk.Frame(root, padding=14)
        surface.pack(fill="both", expand=True)
        connection = ttk.Frame(surface)
        connection.pack(fill="x", pady=(0, 12))
        ttk.Label(connection, text="127.0.0.1 port").pack(side="left")
        ttk.Entry(connection, textvariable=self.port, width=7).pack(side="left", padx=(6, 18))
        ttk.Label(connection, text="RCON password").pack(side="left")
        ttk.Entry(connection, textvariable=self.password, show="*", width=24).pack(side="left", padx=6)
        ttk.Button(connection, text="Check connection", command=lambda: self.refresh(check=True)).pack(side="right")

        tabs = ttk.Notebook(surface)
        tabs.pack(fill="x")
        maps = self.tab(tabs, "Maps")
        match = self.tab(tabs, "Match")
        players = self.tab(tabs, "Players & bots")
        console = self.tab(tabs, "Console")

        row = self.row(maps, "Map")
        self.map_select = ttk.Combobox(row, textvariable=self.map_name)
        self.map_select.pack(side="left", expand=True, fill="x")
        self.button(row, "Refresh list", self.refresh)
        self.button(row, "Change map", self.change_map)
        self.field(maps, "Workshop URL or ID", self.workshop)
        self.field(maps, "Optional map alias", self.alias, "Add map", self.add_map)

        row = self.row(match, "Match controls")
        for name, command in (
            ("Pause", "css_pause"), ("Resume", "css_unpause"),
            ("End warmup", "css_live"), ("Restart match", "css_restart"),
            ("Ready status", "css_readystatus"),
        ):
            self.button(row, name, lambda cmd=command: self.run(cmd))
        row = self.row(match, "Game mode")
        ttk.Combobox(
            row, textvariable=self.mode, state="readonly", width=21,
            values=("bot-game", "bot-game-t", "bot-dm", "bot-dm-pistol"),
        ).pack(side="left")
        self.button(row, "Apply & restart server", self.change_mode)

        row = self.row(players, "Team / bot count")
        ttk.Combobox(row, textvariable=self.team, values=("T", "CT"), width=4, state="readonly").pack(side="left")
        ttk.Spinbox(row, textvariable=self.bot_count, from_=1, to=64, width=5).pack(side="left", padx=6)
        self.button(row, "Add bots", lambda: self.bots("bot_add"))
        self.button(row, "Fill to count", lambda: self.bots("set_bot"))
        self.button(row, "Remove all bots", lambda: self.run("css_kick_bot"))
        self.button(row, "Preview roster", lambda: self.bots("bot_roster"))
        row = self.field(players, "Player user ID", self.user_id, "Kick player", self.kick)
        self.button(row, "List players", lambda: self.run("status"))

        self.field(console, "Announcement", self.announcement, "Send", self.announce)
        self.field(console, "Server command", self.console, "Run", self.raw)

        output_header = ttk.Frame(surface)
        output_header.pack(fill="x", pady=(12, 4))
        ttk.Label(output_header, text="Server output").pack(side="left")
        ttk.Button(output_header, text="Clear", command=self.clear_log).pack(side="right")
        self.output = scrolledtext.ScrolledText(surface, height=12, state="disabled", wrap="word")
        self.output.pack(fill="both", expand=True)
        ttk.Label(surface, textvariable=self.status).pack(anchor="w", pady=(6, 0))
        root.after(80, self.read_results)

    @staticmethod
    def tab(tabs, title):
        page = ttk.Frame(tabs, padding=10)
        tabs.add(page, text=title)
        return page

    @staticmethod
    def row(parent, label):
        row = ttk.Frame(parent)
        row.pack(fill="x", pady=3)
        ttk.Label(row, text=label, width=20).pack(side="left")
        return row

    @staticmethod
    def button(row, label, callback):
        ttk.Button(row, text=label, command=callback).pack(side="left", padx=(5, 0))

    def field(self, parent, label, variable, action=None, callback=None):
        row = self.row(parent, label)
        entry = ttk.Entry(row, textvariable=variable)
        entry.pack(side="left", expand=True, fill="x")
        if action:
            self.button(row, action, callback)
            entry.bind("<Return>", lambda _: callback())
        return row

    def write_log(self, text):
        self.output.configure(state="normal")
        self.output.insert("end", text + "\n")
        self.output.see("end")
        self.output.configure(state="disabled")

    def clear_log(self):
        self.output.configure(state="normal")
        self.output.delete("1.0", "end")
        self.output.configure(state="disabled")

    def error(self, text):
        messagebox.showerror("RepServer Admin", text, parent=self.root)

    def submit(self, label, action, on_success=None):
        if self.busy:
            return
        port = self.port.get().strip()
        password = self.password.get()
        if not re.fullmatch(r"[0-9]{1,5}", port) or not 1 <= int(port) <= 65535:
            self.error("Enter a tunnel port from 1 to 65535.")
            return
        if not re.fullmatch(r"[a-zA-Z0-9]{16,}", password):
            self.error("Enter CS2_RCONPW from the server's .env file.")
            return
        port = int(port)

        def send(command):
            return rcon(port, password, command).replace(password, "[redacted]")

        def worker():
            try:
                self.results.put((on_success, action(send), None))
            except (OSError, ValueError) as exc:
                self.results.put((None, None, str(exc).replace(password, "[redacted]")))

        self.busy = True
        self.status.set(label + "...")
        self.write_log("> " + label)
        threading.Thread(target=worker, daemon=True).start()

    def read_results(self):
        try:
            callback, result, error = self.results.get_nowait()
        except queue.Empty:
            pass
        else:
            self.busy = False
            if error is None:
                self.status.set("Server replied.")
                if callback:
                    callback(result)
                else:
                    self.write_log(result or "Server returned no text.")
            else:
                self.status.set("Request failed. See server output.")
                self.write_log(error)
        self.root.after(80, self.read_results)

    def run(self, command, label=None):
        self.submit(label or command, lambda send: send(command))

    def record_maps(self, response):
        choices = map_choices(response)
        self.map_select.configure(values=choices)
        if choices and self.map_name.get() not in choices:
            self.map_name.set(choices[0])
        self.write_log(response or "The server did not return a map list.")

    def refresh(self, check=False):
        def action(send):
            status = send("css_status") if check else None
            return status, send("css_maps")

        def done(result):
            status, maps = result
            if status is not None:
                self.write_log(status or "RCON replied. Check that the RepServer plugin is loaded.")
            self.record_maps(maps)

        self.submit("Checking connection" if check else "Refreshing map list", action, done)

    def change_map(self):
        name = self.map_name.get().strip().lower()
        if not MAP_NAME.fullmatch(name):
            self.error("Select a map or enter a valid map alias.")
        elif not self.busy and messagebox.askyesno("Change map", f"Load {name} now?", parent=self.root):
            self.run(f"css_map {name}")

    def add_map(self):
        try:
            item_id = workshop_id(self.workshop.get().strip())
        except ValueError as exc:
            self.error(str(exc))
            return
        alias = self.alias.get().strip().lower()
        if alias and not MAP_NAME.fullmatch(alias):
            self.error("Use up to 64 letters, digits or underscores for the map alias.")
            return
        url = f"https://steamcommunity.com/sharedfiles/filedetails/?id={item_id}"
        command = f"css_add_map {url}" + (f" {alias}" if alias else "")

        def action(send):
            result = send(command)
            deadline = time.monotonic() + 15
            while WORKSHOP_PENDING in result and time.monotonic() < deadline:
                time.sleep(0.5)
                result = send(f"css_workshop_result {item_id}")
            if WORKSHOP_PENDING in result:
                result = "Workshop lookup is still pending. Refresh the map list in a moment."
            return result, send("css_maps")

        def done(result):
            outcome, maps = result
            self.write_log(outcome or "No Workshop result received.")
            self.record_maps(maps)

        self.submit(f"Adding Workshop map {item_id}", action, done)

    def change_mode(self):
        mode = self.mode.get()
        if not self.busy and messagebox.askyesno(
            "Change mode", f"Switch to {mode} and restart the server?", parent=self.root,
        ):
            self.run(f"css_mode {mode}")

    def bots(self, action):
        count = self.bot_count.get().strip()
        if not re.fullmatch(r"[0-9]{1,2}", count) or not 1 <= int(count) <= 64:
            self.error("Enter a bot count from 1 to 64.")
            return
        if action != "bot_roster":
            action += "_" + self.team.get().lower()
        self.run(f"css_{action} {count}")

    def kick(self):
        user_id = self.user_id.get().strip()
        if not re.fullmatch(r"[0-9]{1,10}", user_id):
            self.error("Enter the numeric user ID from List players.")
        elif not self.busy and messagebox.askyesno("Kick player", f"Kick user #{user_id}?", parent=self.root):
            self.run(f"kickid {user_id}")

    def announce(self):
        text = self.announcement.get().strip()
        if not text or any(c in text for c in '"\\;\0\r\n'):
            self.error("Enter a message without quotes, backslashes or semicolons.")
            return
        self.run(f'say "{text}"', "Sending announcement")

    def raw(self):
        command = self.console.get().strip()
        if command and not self.busy:
            self.console.set("")
            self.run(command, "Running server command")


if __name__ == "__main__":
    window = tk.Tk()
    AdminPanel(window)
    window.mainloop()
