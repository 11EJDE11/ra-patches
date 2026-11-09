# RA1N version 2

All integers are little endian. HeaderSize is 1312. The first 1096 bytes retain
the replay-enabled client's YRRP version-1 metadata layout. YR-only RNG/object and
checkpoint/stat fields are zero. Initial speed is mapped to the client's nominal
FPS index; completion is bit 0 at 1056, last frame is at 1052.

| Offset | Field |
| --- | --- |
| 1096 | `RA1N` magic (uint32 0x4e314152) |
| 1100 | Container version 2 |
| 1104 | Native event ABI 1 (Westwood 3.03 with restored Seed) |
| 1108 / 1112 | Raw game spawn.ini offset / byte length |
| 1116 / 1120 / 1124 | Event stream offset / compressed length / raw length |
| 1128 / 1132 / 1136 | Check stream offset / compressed length / raw length |
| 1140 | Reserved, zero |
| 1144 | SHA-256 of uncompressed native event stream (32 bytes) |
| 1176 | SHA-256 of uncompressed check stream (32 bytes) |
| 1208 | SHA-256 of the recording executable (32 bytes) |
| 1240 | SHA-256 of raw game spawn.ini (32 bytes) |
| 1272 | SHA-256 of original map bytes (32 bytes) |
| 1304 | Reserved, eight zero bytes |

Payloads are contiguous, in this order:

1. Raw-deflate UTF-8 frontend spawn.ini; stored/raw sizes at 1032/1084.
2. Raw-deflate UTF-8 frontend map; stored/raw sizes at 1036/1088.
3. Original game spawn.ini bytes, without a terminator.
4. Raw-deflate native replay header and event blocks.
5. Raw-deflate frame/RNG/CRC samples, each three uint32 values.

Game INI bytes are losslessly mapped through Latin-1 for the UTF-8 frontend.
Private raw setup bytes drive playback. The map conversion is reversed and its
original-byte digest checked before writing `spawnmap.ini`.

Native header length is `754 + 29*PlayerCount`. Session's fixed prefix is 53
bytes, followed by player records and remaining native header fields. Event
blocks contain a signed int32 count followed by count 20-byte EventClass objects.
Skirmish writes frame zero twice. A check sample corresponds to each Queue_AI
entry, so repeated frame zero is intentional. Check count is the exact playback
ending boundary; raw event streams have no standalone ending marker.

Limits: 32 MiB original setup/map each, 64 MiB frontend UTF-8 each, 256 MiB raw
events, 64 MiB checks, 512 MiB entire file. Files must be finalized, contiguous,
correctly hashed and built for the installed executable. Native event type,
house, count and per-frame bounds are checked before execution.

The metadata schema and RA1 event ABI have independent versions. Keeping a
client-readable envelope does not imply command-stream compatibility with YR,
unmodified Westwood recordings, CO2's rebuilt C++ executable, or the retired
RA1R launcher prototype.
