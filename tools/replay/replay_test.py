"""Prepare and replay an isolated native RA1 recording with its launch inputs.

This is a development harness, not client integration. A match folder retains
the map, rules, binary and spawn options so later client launches cannot replace
them. Native recordings from the unpatched binary are not supported.
"""
import argparse
import configparser
import hashlib
import json
from pathlib import Path
import secrets
import shutil
import struct
import subprocess


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def ini(path):
    config = configparser.ConfigParser(interpolation=None, strict=False, inline_comment_prefixes=(";",))
    config.optionxform = str  # The legacy engine hashes key names case-sensitively.
    config.read(path, encoding="latin1")
    return config


def write_ini(config, path):
    with path.open("w", encoding="latin1") as stream:
        config.write(stream)


def prepare(game, spawn, binary, target, assets=None):
    game, spawn, binary, target = (Path(p).resolve() for p in (game, spawn, binary, target))
    if target.exists():
        raise ValueError("Use a new match directory; existing recordings are never overwritten")
    config = ini(spawn)
    settings = config["Settings"]
    if settings.getboolean("LoadSaveGame", fallback=False):
        raise ValueError("Recording a resumed save needs an initial saved-game snapshot; not supported yet")
    if settings.getboolean("IsSinglePlayer", fallback=False):
        raise ValueError("This first harness targets skirmish and multiplayer")
    scenario = Path(settings["Scenario"].replace("\\", "/"))
    source_map = (game / scenario).resolve()
    if not source_map.is_relative_to(game) or not source_map.is_file():
        raise ValueError("Scenario must name an existing map inside the game directory")
    if settings.getint("Seed", fallback=0) == 0:
        settings["Seed"] = str(secrets.randbelow(0x7FFFFFFE) + 1)
    settings["recording_mode"] = "1"
    settings["LoadSaveGame"] = "No"
    adapter = binary.with_suffix(binary.suffix + ".json")
    if not adapter.is_file():
        raise ValueError("Build manifest is missing beside the test binary")
    build = json.loads(adapter.read_text())
    if digest(binary) != build["output_sha256"]:
        raise ValueError("Test binary no longer matches its build manifest")
    target.mkdir(parents=True)
    for file in game.iterdir():
        if file.is_file() and file.suffix.lower() in (".mix", ".xdp", ".dll", ".ini"):
            shutil.copy2(file, target / file.name)
    if assets:
        for file in Path(assets).glob("*.mix"):
            if not (target / file.name).exists():
                shutil.copy2(file, target / file.name)
    # The legacy spawner's scenario buffer is only 32 bytes. The client normally
    # writes spawnmap.ini; normalize long repository map paths the same way.
    settings["Scenario"] = "spawnmap.ini"
    destination_map = target / "spawnmap.ini"
    shutil.copy2(source_map, destination_map)
    shutil.copy2(binary, target / "ra95.exe")
    shutil.copy2(adapter, target / "replay-build.json")
    # Supply the package's renderer when preparing a fresh repository checkout.
    renderer = game / "Resources/Compatibility/DLL/cnc-ddraw.dll"
    if not (target / "ddraw.dll").exists() and renderer.exists():
        shutil.copy2(renderer, target / "ddraw.dll")
    (target / "ddraw.ini").write_text(
        "[ddraw]\nwindowed=true\nfullscreen=false\nwidth=1024\nheight=768\n"
        "renderer=gdi\ndevmode=true\nmaxfps=60\n", encoding="ascii")
    (target / "REDALERT.INI").write_text(
        "[Options]\nNoCD=yes\nSkipScoreScreen=yes\n[Video]\nScreenWidth=1024\nScreenHeight=768\n", encoding="ascii")
    write_ini(config, target / "record-spawn.ini")
    write_ini(config, target / "spawn.ini")
    excluded = {"spawn.ini", "ddraw.ini", "redalert.ini"}
    hashes = {str(file.relative_to(target)): digest(file)
              for file in target.rglob("*") if file.is_file() and file.name.lower() not in excluded}
    (target / "replay-match.json").write_text(json.dumps(
        dict(format="ra-native-seed-v1", seed=settings.getint("Seed"), hashes=hashes), indent=2) + "\n")
    print(f"Prepared {target}; record with: {target / 'ra95.exe'} -SPAWN")


def inspect_recording(path):
    data = Path(path).read_bytes()
    # Westwood 3.03's exact packed Session.Save(CCFileClass&) layout.
    if len(data) < 53:
        raise ValueError("Truncated session header")
    mode, protocol = data[0], data[1]
    rate = struct.unpack_from("<i", data, 2)[0]
    count = struct.unpack_from("<i", data, 49)[0]
    if mode not in (3, 4, 5) or protocol > 2 or not 1 <= rate <= 60 or not 1 <= count <= 8:
        raise ValueError("Invalid native replay session header")
    header_end = 754 + 29 * count  # includes the restored 4-byte Seed
    if len(data) < header_end:
        raise ValueError("Truncated recording header")
    seed = struct.unpack_from("<I", data, 53 + 29 * count + 8)[0]
    cursor = header_end
    frame = 0 if mode == 5 else (rate if protocol == 2 else 1)
    step = rate if mode != 5 and protocol == 2 else 1
    records, events = 0, 0
    while cursor < len(data):
        if cursor + 4 > len(data):
            raise ValueError(f"Truncated event count at frame {frame}")
        amount = struct.unpack_from("<i", data, cursor)[0]
        cursor += 4
        if not 0 <= amount <= 4096 or cursor + amount * 20 > len(data):
            raise ValueError(f"Invalid/truncated event block at frame {frame}")
        for offset in range(cursor, cursor + amount * 20, 20):
            bits = struct.unpack_from("<I", data, offset + 1)[0]
            if data[offset] > 32 or (bits >> 26 & 31) >= 20 or bits & 0x03FFFFFF != frame:
                raise ValueError(f"Invalid event or frame sequence at frame {frame}")
        cursor += amount * 20
        events += amount
        records += 1
        # The assembly spawner services Queue_AI once before Main_Loop. In a
        # skirmish it records frame zero there AND on the first loop iteration.
        if mode != 5 or records != 1:
            frame += step
    return dict(mode=mode, protocol=protocol, seed=seed, players=count,
                records=records, events=events, last_frame=frame - step if records else None)


def playback(target):
    target = Path(target).resolve()
    manifest = json.loads((target / "replay-match.json").read_text())
    if manifest["format"] != "ra-native-seed-v1":
        raise ValueError("Unsupported test recording bundle")
    for name, expected in manifest["hashes"].items():
        if digest(target / name) != expected:
            raise ValueError(f"Recorded game input changed: {name}")
    result = inspect_recording(target / "ratest.rarec")
    if result["seed"] != manifest["seed"]:
        raise ValueError("Recorded seed differs from the preserved launch seed")
    config = ini(target / "record-spawn.ini")
    config["Settings"]["recording_mode"] = "2"
    write_ini(config, target / "spawn.ini")
    print(json.dumps(result, indent=2))
    return target


def compare_traces(target):
    target = Path(target)
    first = (target / "replay-record.trace").read_bytes()
    second = (target / "replay-play.trace").read_bytes()
    if len(first) % 12 or len(second) % 12 or not first or not second:
        raise ValueError("Missing, empty or truncated trace")
    a = list(struct.iter_unpack("<III", first))
    b = list(struct.iter_unpack("<III", second))
    for index, (recorded, played) in enumerate(zip(a, b)):
        if recorded != played:
            raise ValueError(f"Divergence at sample {index}: record={recorded}, playback={played}")
    if len(b) < len(a):
        raise ValueError(f"Playback ended early: {len(b)} of {len(a)} recorded samples")
    print(f"All {len(a)} recorded frame/RNG/CRC samples match; playback has {len(b)} samples")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    record = commands.add_parser("prepare")
    record.add_argument("--game-dir", required=True, type=Path)
    record.add_argument("--spawn", required=True, type=Path)
    record.add_argument("--binary", required=True, type=Path)
    record.add_argument("--out", required=True, type=Path)
    record.add_argument("--assets", type=Path, help="optional game-assets/main.pack from package checkout")
    play = commands.add_parser("play")
    play.add_argument("match", type=Path)
    play.add_argument("--run", action="store_true")
    inspect = commands.add_parser("inspect")
    inspect.add_argument("recording", type=Path)
    compare = commands.add_parser("compare")
    compare.add_argument("match", type=Path)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.game_dir, args.spawn, args.binary, args.out, args.assets)
    elif args.command == "play":
        match = playback(args.match)
        if args.run:
            result = subprocess.run([str(match / "ra95.exe"), "-SPAWN"], cwd=match)
            print(f"Game exit status: {result.returncode}")
            build = json.loads((match / "replay-build.json").read_text())
            if build.get("trace"):
                compare_traces(match)
            # The legacy package also returns 1 after a completed match. Only
            # accept it after the diagnostic trace confirms complete playback.
            if result.returncode and not (result.returncode == 1 and build.get("trace")):
                raise SystemExit(result.returncode)
        else:
            print(f"Playback ready: {match / 'ra95.exe'} -SPAWN")
    elif args.command == "inspect":
        print(json.dumps(inspect_recording(args.recording), indent=2))
    elif args.command == "compare":
        compare_traces(args.match)
