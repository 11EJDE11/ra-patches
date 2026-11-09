# Westwood RA1 recording and playback

This implementation repairs the native replay path in the Westwood 3.03 binary
used by the RA client package. It does not require rebuilding the game from the
C++ source, or modifying the XNA client's replay code.

## Engine changes

`src/replay.asm` repairs header seed serialization, player-list replacement and
RNG initialization, skirmish frame zero/house execution, multiplayer CRC cadence,
event/count validation, short writes, remote departures and replay sidebar input.
`src/spawn.asm` defers header saving until launch options are complete, uses
recording mode 2 for playback (4 is attract/demo), aborts failed opens/loads and
skips live connections while replaying.

Multiplayer's CRC consumes synchronized RNG. Both recording and playback retain
that cadence; skirmish does not gain an extra draw. Recorded FRAMEINFO transport
history is consumed without treating the offline viewer as a live peer. Remote
EXIT uses the patched live departure handler instead of converting the army to AI.

The assembly spawner queues once before Main_Loop, so skirmish has two frame-zero
records with a simulation update between them. Playback preserves this ordering.
Viewer cameo clicks are ignored before factory/placement UI side effects; the
sidebar still displays the recording player's production state.

## Build against the package executable

The additive builder accepts exactly this pristine package binary:

- package base: `cab197416e7d4934d548419496d99918a7a3886a`;
- input SHA-256: `3d9eef850b9d59191330d3dc4a60ca1a1741663d62bf4c817f1203db813af60e`.

Requires Python 3, NASM and GNU `ld` with `i386pe` support on PATH. From the
ra-patches checkout, with a pristine copy at `original.dat`:

```powershell
python tools/replay/build_test_binary.py original.dat out/ra95.dat
python tools/replay/build_test_binary.py --trace original.dat out/ra95-trace.dat
```

The input is never overwritten. The builder assembles the real replay.asm plus
`shipped-spawner.asm`, appends a PE section and writes an audit manifest beside
the output. The adapter addresses belong to the package's already-linked spawner;
**they are not portable to another ra-patches executable**. Unknown input hashes
are rejected. Diagnostic output adds a writable section and per-frame trace;
the normal build's added section is RX.

Normal Makefile builds use the changes in replay.asm/spawn.asm. Both files assemble
locally. A complete legacy default build has not been validated; runtime evidence
below belongs to the additive build against the exact supported package binary.

## Existing-client integration

Build the native adapter with the .NET SDK:

```powershell
dotnet build tools/replay/ClientReplayLauncher.csproj -c Release -p:ClientBinaryPath=C:/Games/RedAlert/Resources/Binaries
```

The dependency directory must contain the existing client's System.Memory.dll.
Deploy ReplayLauncher.exe and its .config beside ra95.dat and ra95.dat.json.
[CLIENT-INTEGRATION.md](CLIENT-INTEGRATION.md) describes package settings, the
client-compatible metadata envelope, payload fingerprints, limits and UI INI.
There are no client-source changes. The configured recording extension defaults
to `.rarp`; the original client's recording preference, replay list, import/delete,
pruning and Play button are reused.

The launcher maps EnableReplayRecording / ReplayFileOut / ReplayFile onto the
native recording_mode. It preserves the full launch inputs before native startup
can scrub them, writes a portable recording and cleans scratch copies after
success. ReplayKeepTestSnapshots=yes retains development snapshots. Raw capture
folders can be exported with `ReplayLauncher.exe --export-bundle <folder>`.

## Native development harness

Use a complete installed game directory containing its MIX assets and a real
client-generated spawn.ini, or the supplied fixture. Each output must be new:

```powershell
python tools/replay/replay_test.py prepare --game-dir C:/Games/RedAlert --spawn tools/replay/fixtures/skirmish.ini --binary out/ra95-trace.dat --out out/match
```

Run `out/match/ra95.exe -SPAWN` with out/match as its working directory, play and
exit normally. The fixture deploys no units automatically. A Windows test driver
can instead submit MCV deployment/speed commands and exit through the game's
existing ForceExit handler:

```powershell
python tools/replay/smoke_test.py out/match --frames 300
python tools/replay/replay_test.py inspect out/match/ratest.rarec
python tools/replay/replay_test.py play out/match --run
python tools/replay/replay_test.py compare out/match
```

The driver accepts two separately prepared localhost-peer folders for multiplayer
and leaves them sequentially. It operates only on its own hash-verified children.
It requires Windows permission to debug those child processes. The prepared INIs
skip score screens to avoid waiting for UI input in hidden runs.

The native CLI verifies binary/input hashes before playback. Keep the entire
prepared folder for raw ratest.rarec files; portable `.rarp` files carry their own
setup/map and fingerprints. Native streams from the unmodified binary and CO2's
source build have different layouts and must not be interchanged.

The package can return exit status 1 after a complete match. The diagnostic CLI
accepts it only after a full trace comparison; exit status alone does not prove
correct playback. Trace samples contain frame, synchronized RNG seed and native
CRC. The probe preserves RNG/CRC around its diagnostic calculation.

## Automated checks

```powershell
python -m pip install unicorn
python tools/replay/test_hooks.py out/ra95-trace.dat
```

Twenty tests execute the emitted x86 with native calls stubbed: CRC scheduling,
frame zero, house selection, FIFO wrap/filtering, bad headers/counts/events, short
writes, player replacement, startup/transport failure handling, departure parity,
sidebar/attract behavior, cameo cleanup and probe RNG/CRC preservation. The normal
binary passes 19 and skips the trace-only test.

The client/container checks run against the **existing** client assemblies:

```powershell
dotnet build tools/replay/ClientIntegrationChecks.csproj -c Release -p:ClientResourcesPath=C:/Games/RedAlert/Resources
```

Copy ClientIntegrationChecks.exe and its .config to that installed game's root,
then run `ClientIntegrationChecks.exe C:/Games/RedAlert/Resources <replay-filename>`.
Use a completed replay made with the matching installed engine/assets. The checks
read the selected recording and use a fresh GUID directory for their temporary
files. They cover original-reader metadata/setup, extension selection, recording
on/off, pruning, payload/path validation, engine mismatch and YR-format regression.

## Runtime evidence and limits

| Test | Matching frame/RNG/CRC samples |
| --- | --- |
| Skirmish + AI | 4,863 |
| Skirmish + AI, MCV deploy, speed change, exit | 302 |
| Two localhost peers, protocol 2 / rate 3, sequential departures | 320 per peer |
| Same test with package protocol 0 / rate 5 | 329 per peer |
| First client-started skirmish | 2,727 |
| Longer client-started game, including original-client Play-button launch | 11,522 |

The package's shipped client reader also parsed the portable recording and
extracted its setup. Twenty-one client/container checks passed. Actual launcher
runs verified recording disabled creates no recording, recording enabled honors
ReplayFileOut, and portable playback reproduces 302 samples with scratch cleanup.

The CRC is not an exhaustive serialization of game state. These tests are initial
regressions, not coverage of every map/mechanic. Longer network matches, defeat,
disconnects, special weapons, alliances and custom rules need further testing.
Campaigns, resumed saves, seeking and advanced viewer controls are outside this
first implementation. The native stream lacks an explicit ending marker; the
launcher can mark a structurally valid finalized capture but cannot reconstruct a
portable file if it dies before finalization. See the integration document for
nominal duration and interrupted-recording behavior.
