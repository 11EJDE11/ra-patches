# Native RA1 replays using the existing client

The patched `ra95.dat` records and plays containers itself, as the YR spawner
does. The client launches the game directly. There is no replay launcher, CLR
dependency, temporary game copy, or post-exit packaging step.

No changes to the client's replay source or binaries are required. Its recording
toggle, file naming, configurable extension/directory, replay list, import,
deletion, pruning and Play button are reused.

## Package configuration

```ini
[Settings]
ReplaySupport=yes
ReplaysDirectory=Replays
ReplayFileExtension=rarp
GameExecutableNames=ra95.dat
GameLauncherExecutableName=ra95.dat
```

The extension has no leading dot. Enable the existing `btnLoadGame` in
`Resources/MainMenu.ini`. Copy `GameLoadingWindow.ini` to Resources to hide
unimplemented YR viewer controls using existing INI theming. Watch through
**Load Game → Replays → Play**.

Remove forced `recording_mode` and `EnableReplayRecording` values from
`[ForcedSpawnIniOptions]` to follow **Options → Game → Record Replays**.
The existing client's explicit forced recording override remains available.

The game reads the same launch tags as YR:

- `EnableReplayRecording` controls recording of a fresh match.
- `ReplayFileOut` supplies the relative output filename, including its extension.
- `ReplayFile` requests playback of an existing file.

Recording disabled creates no replay. Campaign and saved-game starts proceed
without recording: their initial state is outside this implementation. Output
files are created exclusively; an existing recording is never overwritten.

## Inside the game

Before spawner INI parsing, native C captures the setup and map or validates the
requested replay and restores its recorded setup. The package client already
uses `spawnmap.ini`; playback only writes that fixed map filename and `spawn.ini`.
The recording file's CCFile read/write/seek/size/close operations use the container
provider. Other file operations keep the original game implementation.

Native event serialization and queue execution remain in Westwood's code with
the assembly repairs in `replay.asm`. A frame probe preserves synchronized RNG
and the live CRC while computing a playback check. Every recording includes these
checks, including normal builds. Playback compares each sample and stops at the
recorded end or first divergence. Multiplayer playback skips socket setup and
live peer connections.

Normal match shutdown finalizes compression, checksums, frame count and the clean
completion flag. Failed writes disable recording without ending a live match.
Interrupted files remain incomplete and are rejected for playback. They are not
silently marked complete on the next launch. `replay-native.log` records startup,
completion, sample counts, rejected files and the first state mismatch.

## Format and compatibility

The existing client consumes the `YRRP` version-1 metadata header and raw-deflate
UTF-8 setup/map fields. It skips additive header bytes using HeaderSize. RA1 uses
an `RA1N` version-2 extension and its own compressed native command/check streams;
this is not a YR command recording. See [NATIVE-FORMAT.md](NATIVE-FORMAT.md).

The game validates offsets, lengths, allocation limits, native event structure,
SHA-256 payload hashes and its engine fingerprint. The client retains its
existing recorded-file compatibility prompt through `ReplayFileHashes` in the
embedded setup. Game assets/rules must match. State checks provide a further
failure signal; they do not embed missing assets or make mismatching rules safe.

The abandoned launcher prototype's `RA1R` ZIP format is not supported. Existing
raw development captures can be repacked offline for regression testing; no
conversion utility runs in the production launch path.

## Viewing scope

Playback retains the recording player's house/shroud and the read-only sidebar,
with a free camera. Viewer orders are discarded before simulation effects.
Recorded commands reproduce game-speed changes. YR's observer/house switching,
checkpoint seeking, pause/speed viewer controls, camera following, selections and
chat overlays have not been ported. Those controls are hidden through package
INI, and their launch tags do not replace recorded simulation settings.

Duration uses the recorded initial speed, not measured wall-clock time across
speed changes. The CRC is a useful native state check, not a complete game-state
serialization. Broad real-network and custom-rule regression remains necessary.
