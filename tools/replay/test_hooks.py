"""Execute the assembled package hooks in Unicorn with native calls stubbed.

Usage: python test_hooks.py path/to/ra95-replay.dat
Requires unicorn. This tests actual emitted x86, not a reimplementation of it.
"""
import argparse
from pathlib import Path
import struct
import unittest

from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX
from unicorn.x86_const import UC_X86_REG_ESI, UC_X86_REG_EDI, UC_X86_REG_ESP, UC_X86_REG_EBP, UC_X86_REG_EIP

from build_test_binary import PE

IMAGE = None
STOP = 0x900000


class Engine:
    def __init__(self):
        pe = PE(IMAGE.read_bytes())
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        self.uc.mem_map(pe.base, 0x400000)
        for _, _, rva, size, raw in pe.sections:
            if raw:
                self.uc.mem_write(pe.base + rva, bytes(pe.data[raw:raw + size]))
        self.uc.mem_map(0x800000, 0x20000)
        self.uc.mem_map(STOP, 0x1000)
        self.uc.reg_write(UC_X86_REG_ESP, 0x810000)
        self.uc.reg_write(UC_X86_REG_EBP, 0x818000)
        self.stubs = {}
        self.destination = None
        self.calls = []
        self.writes = []
        self.short_write = None
        self.u32(0x669924, 1)
        self.u32(0x6680C4, 42)
        self.uc.mem_write(0x680151, b"\x01")
        self.uc.hook_add(UC_HOOK_CODE, self.hook)

    def u32(self, address, value=None):
        if value is None:
            return struct.unpack("<I", self.uc.mem_read(address, 4))[0]
        self.uc.mem_write(address, struct.pack("<I", value & 0xFFFFFFFF))

    def ret(self, result=None, arguments=0):
        sp = self.uc.reg_read(UC_X86_REG_ESP)
        target = self.u32(sp)
        self.uc.reg_write(UC_X86_REG_ESP, sp + 4 + arguments)
        if result is not None:
            self.uc.reg_write(UC_X86_REG_EAX, result)
        self.uc.reg_write(UC_X86_REG_EIP, target)

    def hook(self, uc, address, size, _):
        if address in self.destinations:
            self.destination = address
            uc.emu_stop()
        elif address in self.stubs:
            self.calls.append(address)
            self.stubs[address](self)
        elif address == 0x462860:
            length = uc.reg_read(UC_X86_REG_EBX)
            data = bytes(uc.mem_read(uc.reg_read(UC_X86_REG_EDX), length))
            self.writes.append(data)
            self.ret(length - 1 if len(self.writes) == self.short_write else length)
        elif address == 0x462AA8:
            self.calls.append(address)
            self.ret()

    def run(self, address, *destinations, function=False):
        self.destinations = set(destinations or (STOP,))
        if function:
            sp = self.uc.reg_read(UC_X86_REG_ESP) - 4
            self.uc.reg_write(UC_X86_REG_ESP, sp)
            self.u32(sp, STOP)
        self.uc.emu_start(address, 0, count=100000)
        if self.destination is None:
            raise AssertionError(f"Hook did not return/continue: EIP={self.uc.reg_read(UC_X86_REG_EIP):#x}")
        return self.destination


def event(frame=42, house=12, kind=2, executed=False):
    return struct.pack("<BI", kind, frame | house << 26 | int(executed) << 31) + bytes(15)


class HookTests(unittest.TestCase):
    def test_playback_cameo_click_ignores_factory_and_keeps_button_cleanup(self):
        for mode in (0, 1, 2, 6):
            for flags in (1, 8, 16):  # left press, hover/release, right press
                with self.subTest(mode=mode, flags=flags):
                    e = Engine()
                    e.uc.mem_write(0x680151, bytes([mode]))
                    e.uc.reg_write(UC_X86_REG_EAX, 0x808000)
                    e.uc.reg_write(UC_X86_REG_EDX, flags)
                    e.uc.reg_write(UC_X86_REG_EBX, 0x809000)
                    calls = []
                    def cleanup(engine):
                        calls.append(tuple(engine.uc.reg_read(reg) for reg in
                                           (UC_X86_REG_EAX, UC_X86_REG_EDX, UC_X86_REG_EBX)))
                        engine.ret()
                    e.stubs[0x4AC2C0] = cleanup
                    before = e.uc.reg_read(UC_X86_REG_ESP)
                    target = e.run(0x54ECD8, STOP, 0x54ECED, function=True)
                    if mode & 2:
                        self.assertEqual(target, STOP)
                        self.assertEqual(calls, [(0x808000, flags, 0x809000)])
                        self.assertEqual(e.uc.reg_read(UC_X86_REG_EAX), 1)
                        self.assertEqual(e.uc.reg_read(UC_X86_REG_ESP), before)
                        self.assertEqual(e.u32(0x66AB5C), 0)
                    else:
                        self.assertEqual(target, 0x54ECED)
                        self.assertEqual(calls, [])
                        frame = e.uc.reg_read(UC_X86_REG_EBP)
                        self.assertEqual(e.u32(frame - 0x1C), 0x808000)
                        self.assertEqual(e.u32(frame - 0x18), flags)
                        self.assertEqual(e.u32(frame - 0x30), 0x809000)

    def test_sidebar_activation_preserves_attract_restriction(self):
        # Exercise the actual native prologue, patched flag check and control
        # switch. Stop before redraw calls so no drawing stubs can mask errors.
        for mode in (0, 1, 2, 4, 6):
            for active in (0, 1):
                for control in (-1, 0, 1):
                    with self.subTest(mode=mode, active=active, control=control):
                        e = Engine()
                        sidebar = 0x808000
                        e.uc.mem_write(0x680151, bytes([mode]))
                        e.u32(sidebar + 0x15F6, 0xA0 | active)
                        e.uc.reg_write(UC_X86_REG_EAX, sidebar)
                        e.uc.reg_write(UC_X86_REG_EDX, control & 0xFFFFFFFF)
                        target = e.run(0x54DA70, 0x54DAEB, 0x54DC87)
                        expected = active if mode & 4 else (1 - active if control == -1 else control)
                        self.assertEqual(e.u32(sidebar + 0x15F6), 0xA0 | expected)
                        self.assertEqual(e.u32(e.uc.reg_read(UC_X86_REG_EBP) - 0x14), active)
                        self.assertEqual(target, 0x54DC87 if mode & 4 else 0x54DAEB)

    def test_crc_schedule(self):
        for mode, expected in ((0, 0), (5, 0), (4, 1)):
            for frame in (0, 1, 3, 4):
                with self.subTest(mode=mode, frame=frame):
                    e = Engine()
                    e.uc.mem_write(0x67F2B4, bytes([mode]))
                    e.u32(0x6680C4, frame)
                    e.stubs[0x52C044] = lambda engine: engine.ret()
                    e.run(0x52BE87, 0x52BE8C)
                    self.assertEqual(len(e.calls), expected)

    def test_frame_zero(self):
        for mode, expected in ((0, 0x52BED8), (5, 0x52BED8), (4, 0x52C039)):
            e = Engine()
            e.uc.mem_write(0x67F2B4, bytes([mode]))
            self.assertEqual(e.run(0x52BECB, 0x52BED8, 0x52C039), expected)

    def test_house_execution(self):
        for mode, expected in ((0, 0x52BFD6), (5, 0x52BFD6), (4, 0x52C006)):
            e = Engine()
            e.uc.mem_write(0x67F2B4, bytes([mode]))
            self.assertEqual(e.run(0x52BFCD, 0x52BFD6, 0x52C006), expected)

    def test_transport_checks_are_scoped_to_live_play(self):
        for mode, expected in ((0, 0x52BB15), (1, 0x52BB15), (2, 0x52BC63)):
            e = Engine()
            e.uc.mem_write(0x680151, bytes([mode]))
            e.u32(0x818000 - 0x38, 0x12345678)
            self.assertEqual(e.run(0x52BB10, 0x52BB15, 0x52BC63), expected)
            if mode != 2:
                self.assertEqual(e.uc.reg_read(UC_X86_REG_EBX), 0x12345678)

    def test_remote_exit_uses_live_departure_handler(self):
        for mode in (3, 4):
            e = Engine()
            e.uc.mem_write(0x67F2B4, bytes([mode]))
            e.u32(0x818000 - 0x24, 0x808000)
            e.u32(0x808001, 12)
            arguments = []
            def leave(engine):
                arguments.append((engine.uc.reg_read(UC_X86_REG_EAX), engine.uc.reg_read(UC_X86_REG_EDX)))
                engine.ret()
            e.stubs[0x506624] = leave
            e.run(0x52BABA, 0x52BB02)
            self.assertEqual(arguments, [(12, 0)])

    def test_count_validation(self):
        for queued, count, valid in ((0, 0, True), (0, 4096, True), (0, 4097, False),
                                     (0, -1, False), (4095, 1, True), (4095, 2, False)):
            e = Engine()
            e.u32(0x66B068, queued)
            e.u32(0x818000 - 0x1C, count)
            target = e.run(0x52BF3A, 0x52BF3F, 0x52BFB1)
            self.assertEqual(target == 0x52BF3F, valid)
            if valid:
                self.assertEqual(e.uc.reg_read(UC_X86_REG_ECX), 0)
                self.assertEqual(e.uc.reg_read(UC_X86_REG_EDX), count)

    def test_event_validation(self):
        cases = [(event(), True), (event(executed=True), True), (event(kind=32), True),
                 (event(kind=33), False), (event(house=20), False), (event(frame=41), False)]
        for data, valid in cases:
            e = Engine()
            e.uc.mem_write(0x818000 - 0x30, data)
            target = e.run(0x52BF67, 0x52BF75, 0x52BFAA)
            self.assertEqual(target == 0x52BF75, valid)
            if valid:
                self.assertEqual(e.uc.mem_read(0x818000 - 0x2C, 1)[0] & 0x80, 0)

    def test_record_ring_wrap_and_filter(self):
        e = Engine()
        data = [event(), event(executed=True), event(frame=43), event(kind=19)]
        e.u32(0x66B068, len(data))
        e.u32(0x66B06C, 4095)
        for i, item in enumerate(data):
            e.uc.mem_write(0x66B074 + 20 * ((4095 + i) & 4095), item)
        before = e.uc.reg_read(UC_X86_REG_ESP)
        e.run(0x52BD20, function=True)
        self.assertEqual(e.writes, [struct.pack("<i", 2), data[0], data[3]])
        self.assertEqual(e.uc.reg_read(UC_X86_REG_ESP), before)
        self.assertEqual(e.u32(0x669924), 1)

    def test_record_empty(self):
        e = Engine()
        e.run(0x52BD20, function=True)
        self.assertEqual(e.writes, [bytes(4)])

    def test_short_write_disables_only_recording(self):
        for failed_write in (1, 2):
            e = Engine()
            e.u32(0x66B068, 1)
            e.uc.mem_write(0x66B074, event())
            e.short_write = failed_write
            e.run(0x52BD20, function=True)
            self.assertEqual(e.uc.mem_read(0x680151, 1)[0], 0)
            self.assertEqual(e.calls, [0x462AA8])
            self.assertEqual(e.u32(0x669924), 1)

    def test_outgoing_fifo_reset(self):
        e = Engine()
        for address in (0x66AB5C, 0x66AB60, 0x66AB64):
            e.u32(address, 17)
        e.run(0x52BDEC, 0x52BDF1)
        self.assertEqual([e.u32(a) for a in (0x66AB5C, 0x66AB60, 0x66AB64)], [0, 0, 0])

    def test_replaces_players_then_loads_then_seeds(self):
        e = Engine()
        self.stub_header(e)
        e.u32(0x68043A, 0x808000)
        e.u32(0x68044A, 2)
        e.u32(0x808000, 0x808100)
        e.u32(0x808004, 0x808200)
        released = []
        def release(engine):
            released.append(engine.uc.reg_read(UC_X86_REG_EAX))
            engine.ret()
        def load(engine):
            self.assertEqual(engine.u32(0x68044A), 0)
            engine.u32(0x680654, 123456)
            engine.ret(1)
        def seed(engine):
            self.assertEqual(engine.u32(0x680654), 123456)
            engine.ret()
        e.stubs.update({0x5C3945: release, 0x4F87C4: load, 0x4F5EC4: seed})
        e.run(0x6F3861, 0x6F3866)
        self.assertEqual(released, [0x808200, 0x808100])
        self.assertEqual(e.calls[-2:], [0x4F87C4, 0x4F5EC4])

    def stub_header(self, e, players=2, rate=3, size=1000, short=False):
        header = bytearray(53)
        header[0] = 5
        header[1] = 2
        struct.pack_into("<i", header, 2, rate)
        struct.pack_into("<i", header, 49, players)
        e.uc.reg_write(UC_X86_REG_EAX, 0x6800F1)
        e.u32(0x6800F1, 0x809000)
        e.u32(0x809000 + 0x2C, 0xA00020)
        e.u32(0x809000 + 0x28, 0xA00030)
        e.uc.mem_map(0xA00000, 0x1000)
        def read(engine):
            engine.uc.mem_write(engine.uc.reg_read(UC_X86_REG_EDX), bytes(header))
            engine.ret(52 if short else 53)
        e.stubs.update({0x4628B0: read, 0xA00020: lambda engine: engine.ret(size),
                        0xA00030: lambda engine: engine.ret(0)})

    def test_bad_header_never_loads_players(self):
        cases = [dict(players=-1), dict(players=9), dict(rate=0), dict(rate=61),
                 dict(size=100), dict(short=True), dict(players=0)]
        for case in cases:
            e = Engine()
            self.stub_header(e, **case)
            e.u32(0x68044A, 2)
            e.run(0x6F3861, 0x6F3866)
            self.assertEqual(e.uc.reg_read(UC_X86_REG_EAX), 0)
            self.assertEqual(e.u32(0x68044A), 2)
            self.assertNotIn(0x4F87C4, e.calls)
            self.assertEqual(e.calls[-1], 0xA00030)

    def test_failed_header_aborts_spawner(self):
        e = Engine()
        e.uc.reg_write(UC_X86_REG_EAX, 0)
        e.run(0x6F3866, 0x6F3D39)
        self.assertEqual(e.u32(0x669924), 0)

    def test_playback_skips_connections(self):
        for mode, expected in ((0, 1), (1, 1), (2, 0)):
            e = Engine()
            e.uc.mem_write(0x680151, bytes([mode]))
            e.stubs[0x54A4F8] = lambda engine: engine.ret(1)
            e.run(0x6F3CCF, 0x6F3CD4)
            self.assertEqual(len(e.calls), expected)

    def test_header_precedes_scenario(self):
        e = Engine()
        e.stubs[0x4F8718] = lambda engine: engine.ret(1)
        e.stubs[0x53A0A4] = lambda engine: engine.ret(1)
        e.run(0x6F3CAC, 0x6F3CB1)
        self.assertEqual(e.calls, [0x4F8718, 0x53A0A4])

    def test_open_failure_aborts_start(self):
        e = Engine()
        e.run(0x6F387C, 0x6F3D39)
        self.assertEqual(e.u32(0x669924), 0)
        self.assertEqual(e.uc.reg_read(UC_X86_REG_EAX), 0)

    def test_optional_trace_preserves_rng_and_crc(self):
        e = Engine()
        if bytes(e.uc.mem_read(0x528EDC, 1)) != b"\xe9":
            self.skipTest("Diagnostic tracing disabled in this binary")
        e.u32(0x6678E8, 12345)
        e.u32(0x68785C, 0xABCD)
        writes = []
        def create(engine):
            engine.ret(123, arguments=28)
        def crc(engine):
            engine.u32(0x6678E8, 67890)
            engine.u32(0x68785C, 0x12345678)
            engine.ret()
        def write(engine):
            sp = engine.uc.reg_read(UC_X86_REG_ESP)
            writes.append(bytes(engine.uc.mem_read(engine.u32(sp + 8), engine.u32(sp + 12))))
            engine.ret(1, arguments=20)
        e.stubs.update({0x52C044: crc, 0xA00000: create, 0xA00010: write})
        e.uc.mem_map(0xA00000, 0x1000)
        e.u32(0x5E658C, 0xA00000)
        e.u32(0x5E6684, 0xA00010)
        e.run(0x528EDC, 0x528EE6)
        self.assertEqual(e.u32(0x6678E8), 12345)
        self.assertEqual(e.u32(0x68785C), 0xABCD)
        self.assertEqual(writes, [struct.pack("<III", 42, 12345, 0x12345678)])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args = parser.parse_args()
    IMAGE = args.binary
    unittest.main(argv=[__file__], verbosity=2)
