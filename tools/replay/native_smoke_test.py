"""Record/play a native container and exercise rejection paths in the real game.

Uses an isolated developer installation prepared once. No helper is installed or
used by the production replay flow. Starts only its own manifest-verified game.
"""
import argparse
import hashlib
from pathlib import Path
import struct
import subprocess
import time
import zlib

from replay_test import prepare, ini, write_ini, inspect_recording, compare_traces
from smoke_test import Game
from native_replay import load


def run(folder, frames=None, expect_reject=False):
    game = Game(folder)
    injected = False
    deadline = time.monotonic()+90
    print(f"Native test PID {game.process.pid}", flush=True)
    try:
        while game.process.poll() is None and time.monotonic()<deadline:
            try:
                frame=game.u32(0x6680C4)
            except OSError:
                # During ExitProcess the address space can disappear just before
                # the process handle becomes signalled.
                try:
                    game.process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    raise
                break
            if frames is not None and frame>=20 and not injected:
                injected=game.commands(deploy=True)
            if frames is not None and frame>=frames and injected:
                game.quit()
                game.process.wait(timeout=30)
                break
            time.sleep(.03)
        if game.process.poll() is None:
            raise TimeoutError("Owned test game is still available for debugging")
        if not expect_reject and game.process.returncode not in (0,1):
            raise RuntimeError(f"Game failed: {game.process.returncode}")
    finally:
        game.close()


def request(folder, path):
    (folder/"spawn.ini").write_text(f"[Settings]\nReplayFile={path}\n",encoding="utf8")


def compressed(raw):
    encoder=zlib.compressobj(wbits=-15)
    return encoder.compress(raw)+encoder.flush()


def main(args):
    folder=args.out.resolve()
    prepare(args.game_dir,args.spawn,args.binary,folder)
    cfg=ini(folder/"record-spawn.ini")
    # Extension is deliberately different from the package default; the game
    # must honor the client's path and never hardcode a suffix.
    name="Replays/native smoke.repcheck"
    cfg["Settings"].update(EnableReplayRecording="Yes",ReplayFileOut=name,ReplayFile="")
    write_ini(cfg,folder/"spawn.ini")
    run(folder,frames=args.frames)
    recorded=load(folder/name)
    assert recorded["complete"] and recorded["samples"]>=args.frames
    raw=folder/"recorded-events.rarec";raw.write_bytes(recorded["events"])
    assert inspect_recording(raw)["events"]>=2
    log=folder/"replay-native.log"
    assert "recording finalized" in log.read_text()
    request(folder,name);run(folder)
    text=log.read_text()
    assert "playback: all frame/RNG/CRC samples match" in text
    compare_traces(folder)
    print(f"PASS native record/play: {recorded['samples']} samples",flush=True)

    # An explicit client recording-off request also overrides legacy raw mode.
    cfg["Settings"].update(EnableReplayRecording="No",ReplayFileOut="Replays/must-not-exist.rarp",recording_mode="1")
    write_ini(cfg,folder/"spawn.ini");run(folder,frames=100)
    assert not (folder/"Replays/must-not-exist.rarp").exists()
    assert not (folder/"ratest.rarec").exists()
    print("PASS recording toggle off",flush=True)

    original=(folder/name).read_bytes()
    cases={"truncated":original[:100],"truncated-body":original[:-1]}
    for label,offset in [("wrong-engine",1208),("wrong-version",1100),("bad-offset",1116)]:
        value=bytearray(original);value[offset]^=1;cases[label]=value
    value=bytearray(original);value[struct.unpack_from('<I',value,1116)[0]]^=0xff
    cases["corrupt-events"]=value
    # Replace check stream with a valid, correctly hashed stream containing an
    # incorrect state sample. The player must detect it, rather than just EOF.
    value=bytearray(original);checks=bytearray(recorded["checks"]);checks[8]^=1
    offset=struct.unpack_from('<I',value,1128)[0];packed=compressed(checks)
    value=value[:offset]+packed
    struct.pack_into('<I',value,1132,len(packed));value[1176:1208]=hashlib.sha256(checks).digest()
    cases["state-divergence"]=value
    for label,value in cases.items():
        target=f"Replays/test-{label}.rarp";(folder/target).write_bytes(value)
        before=len(log.read_bytes());request(folder,target);run(folder,expect_reject=True)
        added=log.read_bytes()[before:].decode()
        assert ("state mismatch at frame 0" if label=="state-divergence" else "Native replay rejected") in added,added
        print(f"PASS {label}",flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir",type=Path,required=True)
    parser.add_argument("--binary",type=Path,required=True,help="native --trace build")
    parser.add_argument("--out",type=Path,required=True,help="new isolated test installation")
    parser.add_argument("--spawn",type=Path,default=Path(__file__).parent/"fixtures/skirmish.ini")
    parser.add_argument("--frames",type=int,default=300)
    main(parser.parse_args())
