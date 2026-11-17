"""Package RepServer's own bot profiles as a small, single-file VPK."""
import struct
import sys
import zlib
from pathlib import Path


def read_profiles(path):
    names = set()
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), start=1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, separator, role = (piece.strip() for piece in line.partition("|"))
        role = role.lower() if separator else "rifle"
        if (
            not name
            or len(name.encode("utf-8")) > 31
            or any(c in name for c in '"\\;')
            or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in name)
            or role not in ("rifle", "sniper")
            or name.lower() in names
        ):
            raise ValueError(f"{path}:{number}: ogiltigt eller dubbelt botnamn eller ogiltig roll")
        names.add(name.lower())
        yield name, role


def build_database(profiles):
    blocks = [
        "Default\nSkill = 85\nAggression = 65\nTeamwork = 90\nReactionTime = 0.25\nDifficulty = EXPERT\nEnd",
        "Template RepRifle\nWeaponPreference = ak47\nWeaponPreference = m4a1\nWeaponPreference = famas\nEnd",
        "Template RepSniper\nWeaponPreference = awp\nWeaponPreference = ssg08\nEnd",
    ]
    for name, role in profiles:
        template = "RepSniper" if role == "sniper" else "RepRifle"
        blocks.append(f'{template} "{name}"\nEnd')
    return ("\n\n".join(blocks) + "\n").encode("utf-8")


def package_database(data):
    # Extension, root directory, file stem, and an embedded data entry.
    entry = struct.pack("<IHHIIH", zlib.crc32(data), 0, 0x7FFF, 0, len(data), 0xFFFF)
    tree = b"db\0 \0botprofile\0" + entry + b"\0\0\0"
    header = struct.pack("<7I", 0x55AA1234, 2, len(tree), len(data), 0, 0, 0)
    return header + tree + data


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Använd: build-bot-profile.py bot-names.txt botprofile.vpk")
    try:
        profiles = list(read_profiles(Path(sys.argv[1])))
        if not profiles:
            raise ValueError("Botlistan är tom.")
        Path(sys.argv[2]).write_bytes(package_database(build_database(profiles)))
    except (OSError, ValueError) as error:
        raise SystemExit(str(error)) from error
