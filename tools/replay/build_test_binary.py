"""Build an additive replay test patch against one verified package executable.

Uses the same src/replay.asm as a normal ra-patches build, plus the package's
spawner adapter. Never patches the input in place. Python and GNU binutils/NASM
are required; the obsolete full ra-patches build environment is not required.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile

SHIPPED_SHA256 = "3d9eef850b9d59191330d3dc4a60ca1a1741663d62bf4c817f1203db813af60e"
ROOT = Path(__file__).resolve().parents[2]


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


class PE:
    def __init__(self, data):
        self.data = bytearray(data)
        self.pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[self.pe:self.pe + 4] != b"PE\0\0":
            raise ValueError("Not a PE executable")
        self.coff = self.pe + 4
        self.count = struct.unpack_from("<H", data, self.coff + 2)[0]
        self.opt = self.coff + 20
        if struct.unpack_from("<H", data, self.opt)[0] != 0x10B:
            raise ValueError("Expected PE32")
        self.base = self.u32(self.opt + 28)
        self.section_alignment = self.u32(self.opt + 32)
        self.file_alignment = self.u32(self.opt + 36)
        self.image_size = self.u32(self.opt + 56)
        self.table = self.opt + struct.unpack_from("<H", data, self.coff + 16)[0]
        self.sections = []
        for i in range(self.count):
            offset = self.table + 40 * i
            name, size, rva, raw_size, raw = struct.unpack_from("<8sIIII", data, offset)
            self.sections.append((name.rstrip(b"\0"), size, rva, raw_size, raw))

    def u32(self, offset):
        return struct.unpack_from("<I", self.data, offset)[0]

    def offset(self, address, length=1):
        rva = address - self.base
        for _, _, start, raw_size, raw in self.sections:
            if start <= rva and rva + length <= start + raw_size and raw:
                return raw + rva - start
        raise ValueError(f"Patch has no file-backed section: {address:#x}+{length}")

    def section_data(self, name):
        for n, size, _, raw_size, raw in self.sections:
            if n == name:
                return bytes(self.data[raw:raw + min(size, raw_size)])
        raise ValueError(f"Missing section {name!r}")

    def append_code(self, code, writable=False):
        header = self.table + 40 * self.count
        if header + 40 > self.u32(self.opt + 60):
            raise ValueError("No room for another section header")
        if any(self.data[header:header + 40]):
            raise ValueError("New section header would overwrite existing data")
        rva = align(self.image_size, self.section_alignment)
        raw = align(len(self.data), self.file_alignment)
        raw_size = align(len(code), self.file_alignment)
        self.data.extend(bytes(raw - len(self.data)))
        self.data.extend(code + bytes(raw_size - len(code)))
        struct.pack_into("<8sIIIIIIHHI", self.data, header,
                         b".replay", len(code), rva, raw_size, raw, 0, 0, 0, 0,
                         0xE0000020 if writable else 0x60000020)
        struct.pack_into("<H", self.data, self.coff + 2, self.count + 1)
        struct.pack_into("<I", self.data, self.opt + 4, self.u32(self.opt + 4) + raw_size)
        struct.pack_into("<I", self.data, self.opt + 56,
                         align(rva + len(code), self.section_alignment))
        struct.pack_into("<I", self.data, self.opt + 64, 0)  # checksum
        return self.base + rva


def build(source, output, trace=False):
    source, output = Path(source).resolve(), Path(output).resolve()
    if source == output:
        raise ValueError("Input and output must be different files")
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHIPPED_SHA256:
        raise ValueError("Unrecognized input executable; refusing to apply fixed-address hooks")
    image = PE(data)
    payload_base = image.base + align(image.image_size, image.section_alignment)
    for tool in ("nasm", "ld"):
        if not shutil.which(tool):
            raise ValueError(f"Required build tool is not on PATH: {tool}")
    with tempfile.TemporaryDirectory(prefix="ra-replay-") as temporary:
        work = Path(temporary)
        objects = []
        sources = [("replay", ROOT / "src/replay.asm"),
                   ("spawner", ROOT / "tools/replay/shipped-spawner.asm")]
        if trace:
            sources.append(("trace", ROOT / "tools/replay/trace.asm"))
        for name, asm in sources:
            obj = work / f"{name}.o"
            subprocess.run(["nasm", "-f", "elf", "-I", str(ROOT / "inc") + "/",
                            "-o", str(obj), str(asm)], check=True)
            objects.append(str(obj))
        script = work / "test.lds"
        script.write_text(f"""SECTIONS {{
          .replay {payload_base:#x} : {{ *(.text) *(.rdata) *(.data) *(.bss) }}
          .patch ALIGN(0x1000) : SUBALIGN(1) {{ *(.patch) }}
        }}
        _recording_mode = 0x680151;
        _recording_file = 0x6800f1;
        """, encoding="ascii")
        linked = work / "linked.exe"
        subprocess.run(["ld", "-m", "i386pe", "--image-base=0x400000",
                        "--disable-reloc-section", "--entry=_Replay_Load_Header",
                        "-T", str(script), "-o", str(linked), *objects], check=True)
        linked_image = PE(linked.read_bytes())
        payload = linked_image.section_data(b".replay")
        patches = linked_image.section_data(b".patch")
    audit = []
    used = set()
    cursor = 0
    while cursor < len(patches):
        if len(patches) - cursor < 8:
            raise ValueError("Truncated patch descriptor")
        address, length = struct.unpack_from("<II", patches, cursor)
        cursor += 8
        if address == 0 and length == 0 and not any(patches[cursor:]):
            break  # PE linker section-end padding
        if length == 0:
            continue  # hack(start, start + 5) emits an empty @CLEAR record
        if cursor + length > len(patches):
            raise ValueError(f"Invalid patch descriptor at {cursor - 8:#x}: "
                             f"{address:#x}, {length}; section length {len(patches)}")
        replacement = patches[cursor:cursor + length]
        cursor += length
        span = set(range(address, address + length))
        if span & used:
            raise ValueError(f"Overlapping patches at {address:#x}")
        used.update(span)
        offset = image.offset(address, length)
        before = bytes(image.data[offset:offset + length])
        image.data[offset:offset + length] = replacement
        audit.append(dict(address=hex(address), before=before.hex(), after=replacement.hex()))
    if image.append_code(payload, writable=trace) != payload_base:
        raise ValueError("Payload location changed")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(image.data)
    manifest = dict(input_sha256=SHIPPED_SHA256,
                    output_sha256=hashlib.sha256(image.data).hexdigest(),
                    payload_address=hex(payload_base), payload_size=len(payload), trace=trace, patches=audit)
    output.with_suffix(output.suffix + ".json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Built {output}: {len(audit)} patches, {len(payload)} bytes of replay code")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--trace", action="store_true", help="write per-frame determinism diagnostic traces")
    args = parser.parse_args()
    build(args.input, args.output, trace=args.trace)
