"""Package the round announcer clips as Source 2 PCM sound resources."""

import hashlib
import struct
import sys
import wave
import zlib
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


CLIPS = ("ace", "ace-stealer", "acestealelele")


def compile_sound(path):
    with wave.open(str(path), "rb") as sound:
        channels = sound.getnchannels()
        rate = sound.getframerate()
        frames = sound.getnframes()
        if sound.getsampwidth() != 2 or channels not in (1, 2) or not 0 < rate <= 65535 or not frames:
            raise ValueError(f"{path}: expected a nonempty 16-bit PCM WAV with one or two channels")
        samples = sound.readframes(frames)
        if len(samples) != frames * channels * 2:
            raise ValueError(f"{path}: incomplete PCM sample data")

    # The legacy VSND v4 DATA block describes PCM stored after the resource header.
    # Layout reference: ValveResourceFormat/Resource/ResourceTypes/Sound.cs.
    metadata = struct.pack(
        "<HBBiIfIIIIIIii",
        rate, 0, channels, -1, frames, frames / rate,
        0, 0, 0, len(samples), 0, 0, 0, -1,
    )
    data_start = 32
    header = struct.pack("<IHHII", data_start + len(metadata), 12, 4, 8, 1)
    block = struct.pack("<4sII", b"DATA", data_start - 20, len(metadata))
    return (header + block).ljust(data_start, b"\0") + metadata + samples


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: build-round-sounds.py <wav-directory> <output.zip|output.vpk>")

    source, output = map(Path, sys.argv[1:])
    sounds = {name: compile_sound(source / f"{name}.wav") for name in CLIPS}
    if output.suffix == ".vpk":
        output.write_bytes(package_vpk(sounds))
    elif output.suffix == ".zip":
        with ZipFile(output, "w", compression=ZIP_DEFLATED) as archive:
            for name, sound in sounds.items():
                archive.writestr(f"sounds/repserver/{name}.vsnd_c", sound)
    else:
        raise ValueError("The output extension must be .zip or .vpk")


def package_vpk(sounds):
    tree = bytearray(b"vsnd_c\0sounds/repserver\0")
    payload = bytearray()
    for name, sound in sorted(sounds.items()):
        tree.extend(name.encode("ascii") + b"\0")
        tree.extend(struct.pack("<IHHIIH", zlib.crc32(sound), 0, 0x7FFF, len(payload), len(sound), 0xFFFF))
        payload.extend(sound)
    tree.extend(b"\0\0\0")
    # VPK v2 checksums cover the directory, the empty external-archive index,
    # and the entire unsigned package up to the final checksum.
    def md5(data):
        return hashlib.md5(data, usedforsecurity=False).digest()

    header = struct.pack("<7I", 0x55AA1234, 2, len(tree), len(payload), 0, 48, 0)
    package = header + tree + payload + md5(tree) + md5(b"")
    return package + md5(package)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, wave.Error) as error:
        raise SystemExit(str(error)) from error
