"""Windows-only automated developer smoke test for prepared match directories.

Starts only the manifest-verified test executable. Through its outgoing command
queue, submits DEPLOY (unit 0) and GAMESPEED, then asks the existing ForceExit
handler to leave normally. Playback is a second process and its full diagnostic
trace must match. Give two prepared localhost peers to exercise multiplayer.
Requires permission to debug the child processes, plus a --trace test binary.
"""
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
from pathlib import Path
import struct
import subprocess
import time

from replay_test import digest, playback, compare_traces, inspect_recording

k = c.WinDLL("kernel32", use_last_error=True)
n = c.WinDLL("ntdll")
k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
k.OpenProcess.restype = w.HANDLE
k.CloseHandle.argtypes = [w.HANDLE]
for name in ("ReadProcessMemory", "WriteProcessMemory"):
    getattr(k, name).argtypes = [w.HANDLE, c.c_void_p, c.c_void_p, c.c_size_t, c.POINTER(c.c_size_t)]
n.NtSuspendProcess.argtypes = [w.HANDLE]
n.NtResumeProcess.argtypes = [w.HANDLE]


class Game:
    def __init__(self, match):
        self.match = Path(match).resolve()
        build = json.loads((self.match / "replay-build.json").read_text())
        if not build.get("trace") or digest(self.match / "ra95.exe") != build["output_sha256"]:
            raise ValueError("Smoke tests require the manifest-verified diagnostic binary")
        startup = subprocess.STARTUPINFO()
        startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        self.process = subprocess.Popen([str(self.match / "ra95.exe"), "-SPAWN"],
                                        cwd=self.match, startupinfo=startup)
        self.handle = k.OpenProcess(0x1838, False, self.process.pid)
        if not self.handle:
            self.process.terminate()
            raise c.WinError(c.get_last_error())

    def read(self, address, length=4):
        buffer = c.create_string_buffer(length)
        count = c.c_size_t()
        if not k.ReadProcessMemory(self.handle, address, buffer, length, c.byref(count)):
            raise c.WinError(c.get_last_error())
        return bytes(buffer)

    def u32(self, address):
        return struct.unpack("<I", self.read(address))[0]

    def write(self, address, data):
        count = c.c_size_t()
        if not k.WriteProcessMemory(self.handle, address, c.c_char_p(data), len(data), c.byref(count)):
            raise c.WinError(c.get_last_error())

    def commands(self, deploy=False):
        if n.NtSuspendProcess(self.handle) < 0:
            raise RuntimeError("Could not suspend the test child")
        try:
            if self.u32(0x66AB5C):
                return False
            frame = self.u32(0x6680C4) + 30
            house = self.u32(self.u32(0x669958) + 1)
            commands = [(10, 2)]
            if deploy:
                commands.append((7, 0x1C000000))  # RTTI_UNIT, heap slot 0 (first MCV)
            for slot, (kind, value) in enumerate(commands):
                data = struct.pack("<BII", kind, frame | house << 26, value) + bytes(11)
                self.write(0x66AB68 + slot * 20, data)
            self.write(0x66AB60, bytes(4))
            self.write(0x66AB64, struct.pack("<I", len(commands)))
            self.write(0x66AB5C, struct.pack("<I", len(commands)))
            print(f"PID {self.process.pid}: queued {commands} for house {house}, frame >= {frame}", flush=True)
            return True
        finally:
            n.NtResumeProcess(self.handle)

    def quit(self):
        self.write(0x704030, b"\x01")  # already-shipped normal exit request handler

    def close(self):
        k.CloseHandle(self.handle)


def wait_exit(games, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if all(g.process.poll() is not None for g in games):
            return
        time.sleep(0.1)
    raise RuntimeError("A test game did not exit normally; its process remains available for debugging")


def run(matches, frames):
    if len(matches) > 2:
        raise ValueError("Use one skirmish or two localhost multiplayer match folders")
    games = [Game(match) for match in matches]
    injected = set()
    deadline = time.monotonic() + 90
    try:
        while time.monotonic() < deadline:
            if any(g.process.poll() is not None for g in games):
                raise RuntimeError("A recording process exited before the test commands completed")
            current = [g.u32(0x6680C4) for g in games]
            for index, game in enumerate(games):
                if index not in injected and current[index] >= 20:
                    if game.commands(deploy=index == 0):
                        injected.add(index)
            if min(current) >= frames and len(injected) == len(games):
                break
            time.sleep(0.05)
        else:
            raise RuntimeError(f"Games did not reach frame {frames}; last frames={current}")
        # Let the first quitter finish while its peer still services traffic.
        # Simultaneously terminating both peers stresses the legacy last-packet
        # shutdown handshake rather than the record/playback path.
        games[0].quit()
        wait_exit(games[:1], 90)
        for game in games[1:]:
            if game.process.poll() is None:
                game.quit()
        wait_exit(games[1:], 90)
    finally:
        for game in games:
            game.close()
    for match in matches:
        print(json.dumps(inspect_recording(Path(match) / "ratest.rarec")), flush=True)
        playback(match)
    viewers = [Game(match) for match in matches]
    try:
        wait_exit(viewers, 90)
    finally:
        for game in viewers:
            game.close()
    for match in matches:
        compare_traces(match)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("matches", nargs="+", type=Path)
    parser.add_argument("--frames", type=int, default=300)
    args = parser.parse_args()
    if not 100 <= args.frames <= 3000:
        parser.error("--frames must be between 100 and 3000")
    run(args.matches, args.frames)
