"""Inspect the native RA1N container. This is a development tool, not a launcher."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zlib

HEADER_SIZE = 1312


def load(path):
    data = Path(path).read_bytes()
    if len(data) < HEADER_SIZE:
        raise ValueError("Truncated header")
    def u(offset):
        return struct.unpack_from("<I", data, offset)[0]
    if (u(0), u(4), u(8), u(1096), u(1100), u(1104)) != (
            0x50525259, 1, HEADER_SIZE, 0x4E314152, 2, 1):
        raise ValueError("Unsupported native replay")
    def chunk(offset, stored, raw, limit):
        if not 0 < raw <= limit or not 0 < stored <= len(data)-offset:
            raise ValueError("Invalid payload bounds")
        decoder = zlib.decompressobj(-15)
        value = decoder.decompress(data[offset:offset+stored], raw+1)
        if len(value) != raw or not decoder.eof or decoder.unused_data:
            raise ValueError("Invalid compressed payload")
        return value
    spawn = chunk(HEADER_SIZE, u(1032), u(1084), 64*1024*1024)
    map_utf8 = chunk(HEADER_SIZE+u(1032), u(1036), u(1088), 64*1024*1024)
    map_raw = map_utf8.decode("utf8").encode("latin1")
    raw_offset, raw_size = u(1108), u(1112)
    if not 0 < raw_size <= 32*1024*1024 or raw_offset != HEADER_SIZE+u(1032)+u(1036):
        raise ValueError("Invalid raw setup bounds")
    raw = data[raw_offset:raw_offset+raw_size]
    if u(1116) != raw_offset+raw_size or u(1128) != u(1116)+u(1120) or u(1128)+u(1132) != len(data):
        raise ValueError("Invalid stream layout")
    events = chunk(u(1116), u(1120), u(1124), 256*1024*1024)
    checks = chunk(u(1128), u(1132), u(1136), 64*1024*1024)
    for value, offset in [(events,1144), (checks,1176), (raw,1240), (map_raw,1272)]:
        if hashlib.sha256(value).digest() != data[offset:offset+32]:
            raise ValueError("Payload hash mismatch")
    if len(checks)%12:
        raise ValueError("Partial state sample")
    return dict(header=data[:HEADER_SIZE], spawn=spawn, raw_spawn=raw, map=map_raw,
                events=events, checks=checks, complete=bool(u(1056)&1), frames=u(1052), seed=u(20),
                engine_sha256=data[1208:1240].hex(), samples=len(checks)//12)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("replay", type=Path)
    args = parser.parse_args()
    result = load(args.replay)
    print(json.dumps({k:v for k,v in result.items() if not isinstance(v,bytes)}, indent=2))
