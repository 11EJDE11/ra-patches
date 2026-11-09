# RA1 replays using the existing client

No changes to the client's replay source are required. This implementation adapts
RA1 to the metadata envelope the replay-enabled client already reads. Recording,
file naming, extension selection, listing, import, deletion, pruning and launching
all use the existing client code.

## Package configuration

In `Resources/ClientDefinitions.ini`:

```ini
[Settings]
ReplaySupport=yes
ReplaysDirectory=Replays
ReplayFileExtension=rarp
GameExecutableNames=ra95.dat
GameLauncherExecutableName=ReplayLauncher.exe
```

The extension has no leading dot and can be changed. There is no new
`ReplayFormat` client setting. Remove forced `recording_mode` and
`EnableReplayRecording` values from `[ForcedSpawnIniOptions]` if the player's
**Options → Game → Record Replays** checkbox should control recording. The existing
client still allows an explicit forced `EnableReplayRecording` override.

The RA package hides its Load Game button. Set `Visible=true` and `Enabled=true`
for `btnLoadGame` in `Resources/MainMenu.ini`. Copy the supplied
`GameLoadingWindow.ini` into Resources to hide unsupported YR viewing controls
through the client's existing INI theming. Replays are opened through **Load Game
→ Replays → Play**.

The supported package's shipped client reader parses the metadata and extracts
the setup without modification. Older client builds without replay support need
an ordinary replay-enabled client update.

## Launcher

Use the repaired Westwood binary from this work and build the adapter:

```powershell
dotnet build tools/replay/ClientReplayLauncher.csproj -c Release
```

The SDK project takes `ClientBinaryPath`, pointing at Resources/Binaries or a
normal net48 client build output containing `System.Memory.dll`. Its default is
the sibling cncnet-ra-client-package checkout. It does not depend on client
source. Deploy `ReplayLauncher.exe` and its `.config` beside
`ra95.dat`; its dependency resolver uses the client's existing Resources/Binaries.

The launcher reads the tags the client already writes:

- `EnableReplayRecording` controls a new recording;
- `ReplayFileOut` supplies the configured relative filename;
- `ReplayFile` requests playback, with recording disabled.

It translates those into native RA1 `recording_mode` values. With recording off,
it launches a normal game and creates no recording snapshot. With recording on,
it captures the settings/map before native startup scrubs them and packages the
finished event stream. Campaign and saved-game starts continue normally with
recording skipped; the current recorder supports fresh skirmish and multiplayer.

Temporary game copies are removed after success by default. The renderer's
read-only attribute is cleared only on disposable copies before cleanup. Set
`ReplayKeepTestSnapshots=yes` for development to retain ReplayRecordings and
ReplayPlayback folders. These debug folders are outside the client's replay
storage limits. Existing
captures and backups are preserved.

## Client-compatible container

The client's existing reader only consumes metadata and embedded launch inputs;
it does not decode the game's command stream. The RA1 file therefore consists of:

```
[1096-byte existing client metadata header]
[32-byte RA1R extension]                     HeaderSize = 1128
[raw-deflate spawn.ini]
[raw-deflate spawnmap.ini]
[RA1 ZIP payload]
```

The existing prefix is `YRRP`, metadata version 1. HeaderSize, compressed/raw INI
sizes, recording time, completion flag, last frame and speed fields have exactly
the meanings the client expects. RA1's nominal recording speed is mapped to the
equivalent existing client speed index for duration display. Header fields used
only by the YR simulation are zero and never used by the RA1 player.

This is **client metadata compatibility**, not a YR recording: the game-specific
payload is tagged `RA1R` at offset 1096 and must be played by the RA1 launcher.
Its extension contains the RA1 container version (1), native ABI version (1), a
reserved zero, and 64-bit ZIP offset/length. The original client skips the extra
header bytes using HeaderSize, as it already does for additive header fields.

The ZIP contains `replay.ini`, UTF-8 `spawn.ini`, original `spawnmap.ini`,
`events.rarec` and optional diagnostic `checks.trace`. `replay.ini` declares
`Format=CnCNet-RA1-Replay`, `Version=1`, `RecordedAt`, `TotalFrames`, `FrameRate`,
`Complete`, `Seed`, `[PayloadHashes]` and `[GameFileHashes]`. Hashes use SHA-256;
game inputs are relative paths encoded as numeric-key `path|hash` values. The map
and spawn data are repeated inside the verified RA1 payload so the native player
uses its own preserved inputs independently of viewer settings the client writes.

No engine or MIX archives are embedded. The installed game files must match the
recording's fingerprints. The original client retains its existing mismatch
prompt; the RA1 launcher additionally enforces its own fingerprints before starting
the engine. An RA1 replay cannot be played as a YR replay by renaming its extension.

Only named ZIP entries are read; paths are never extracted from arbitrary entries.
Limits are 32 entries, 512 MB total declared raw ZIP data, 128 KB manifest, 32 MB
per setup payload, 256 MB native events and 64 MB diagnostic trace. The reader
validates offsets/lengths, hashes, paths and the native event sequence. Future
incompatible formats must also change the client metadata version so the existing
client lists them as unsupported rather than offering Play.

The last frame differs from the native record count because skirmish records frame
zero twice. Duration is nominal rather than measured wall-clock time when speed
changes or a machine runs slowly. Complete means the launcher finalized a valid
stream after a normal native exit status (0 or 1); the native stream itself still
lacks an explicit ending marker. A failed process can leave a recoverable scratch
recording instead of a portable file.

## Viewing and validation

Playback keeps the recording player's house, shroud and free camera, with the
repaired read-only sidebar. YR-only observer, follow-camera, selection, chat, speed
and checkpoint controls are hidden by package INI. The launcher ignores those
unimplemented viewer tags; they do not change the simulation. Their eventual RA1
implementation is separate engine work.

Diagnostic recordings compare frame/RNG/CRC samples and log the result in
ReplayLauncher.log and ReplayPlayback/latest-validation.txt. Early stopping reports
a matching prefix; divergence reports its first sample. Playback publishes no new
recording and does not overwrite live match statistics.

`ClientIntegrationChecks.cs` runs against the actual unchanged client reader and
manager. It covers listing, metadata/setup, extension configuration, recording
on/off, pruning, payload/path validation, unsupported versions and YR regression
fixtures. Native launcher checks cover recording on/off and scratch cleanup. The
real 11,522-sample game also serves as the end-to-end Play-button regression.
