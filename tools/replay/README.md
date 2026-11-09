# Westwood RA1 native recording and playback

This repairs recording/playback in the Westwood 3.03 binary shipped by the client
package. Native C handles the replay container inside the patched game; assembly
connects it to the original queue, header and file functions. The client launches
`ra95.dat` directly, using its existing replay UI and settings unchanged.

## Build

Requires Python 3, NASM, 32-bit-capable GCC and GNU ld with i386pe support on PATH.
The additive builder accepts only the pristine package binary from base commit
`cab197416e7d4934d548419496d99918a7a3886a`, SHA-256
`3d9eef850b9d59191330d3dc4a60ca1a1741663d62bf4c817f1203db813af60e`.

```powershell
python tools/replay/build_test_binary.py original.dat out/ra95.dat
python tools/replay/build_test_binary.py original.dat out/ra95-trace.dat --trace
```

Input is never overwritten. The builder assembles the same replay code used by
the Makefile, compiles the native C provider/miniz, links with no runtime imports,
and adds separate RX code/constants and RW data sections. It writes a hash and
patch audit manifest. Package-spawner adapter addresses are valid only for this
hash. `--trace` additionally writes development trace files; native state checks
are included in both builds.

Full Makefile builds include the same native modules and corresponding spawner
changes. Modules assemble/compile locally, but a complete legacy default build
has not been validated. Runtime evidence belongs to the additive package build.
The miniz amalgamation/license is vendored from the YR spawner's existing copy.
SHA-256 uses this repository's public-domain implementation with private symbols.

## Engine repairs

`src/replay.asm` restores seed serialization, replaces the spawner player list
before loading the header, reseeds after load, and repairs skirmish frame-zero and
house execution. Multiplayer preserves the CRC's synchronized RNG cadence,
consumes FRAMEINFO without offline transport comparisons, and handles remote EXIT
through the live patched departure handler. Queue writes, counts and events are
checked. The recorded player's sidebar remains visible; cameo clicks are ignored
before factory/placement changes. Viewer commands never enter the simulation.

`src/replay-native.c` records setup before spawner scrubbing, streams compression,
validates playback and compares frame/RNG/CRC samples. `replay-native-hooks.asm`
bridges Watcom register calls to cdecl and redirects only the recording object.
`src/spawn.asm` binds the provider, defers header saving until options are loaded,
aborts invalid playback, and skips live networking. `shipped-spawner.asm` adapts
the already-linked package spawner to the same behavior.

See [CLIENT-INTEGRATION.md](CLIENT-INTEGRATION.md) for package settings, supported
viewer behavior and limits; [NATIVE-FORMAT.md](NATIVE-FORMAT.md) documents the file.
There is no external replay launcher or client-source dependency.

## Checks

```powershell
python -m pip install unicorn
python tools/replay/test_hooks.py out/ra95-trace.dat
python tools/replay/native_smoke_test.py --game-dir C:/Games/RedAlert --binary out/ra95-trace.dat --out out/native-smoke
python tools/replay/native_replay.py "out/native-smoke/Replays/native smoke.repcheck"
```

Use PowerShell quoting around the replay path with spaces. The real-game smoke
test creates one isolated developer installation, records MCV deployment and a
speed event, plays through the native provider, compares every state sample and
the independent diagnostic traces, checks recording off, and exercises malformed
files and an intentionally divergent but correctly hashed state sample. It uses
only its own manifest-verified child processes. Its temporary developer install
is separate from production, which runs directly in the client game directory.

Twenty-one emitted-x86 tests cover queue/RNG/header/sidebar and offline startup. The raw-stream
development tools (`replay_test.py`, `smoke_test.py`, `--legacy-raw`) remain useful
for individual queue regressions; they are not installed or used by the client.

The native provider also reproduced all 11,522 frame/RNG/CRC samples from the
longer local human recording. Fresh native recording/playback reproduces its
samples as well. Earlier raw-queue regressions include two-peer protocol 2/rate 3
and protocol 0/rate 5 games. Broad real-network, custom-map, defeat, alliance and
special-weapon coverage remains review work. Campaigns, resumed saves and YR's
advanced viewer controls are outside this initial port.
