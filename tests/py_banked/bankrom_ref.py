"""Python reference twin of System12.Runtime.Banked.BankedRomController.

Ported line-by-line from MAME 0.264 / mamedev/mame@f8f6b22 (src/mame/namco/namcos12.cpp)
and src/devices/cpu/psx/dma.cpp (the channel-5 read path), see reports/agent04_banked_rom.md.

Scope (tektagt_state, coh700b board):
  0x1f000000      bankoffset_w (16-bit handler, nopr for reads)           -> memory_bank "mainbank" entry
  0x1fa00000..    bankr("mainbank"): 2 MiB window onto bankedroms (28 entries of 0x200000 for tektagt)
  0x1f700000..    dmaoffset_w (write, 64 KiB), tektagt_protection_3_r (read @ +0; the 0x800000 mirror is shadowed by ROM)
  0x1fb00000      tektagt_protection_1_r/w   (overrides the bank window)
  0x1fb80000      tektagt_protection_2_r/w   (overrides the bank window)
  DMA channel 5   namcos12_rom_read(), reached through psxdma_device::write() (class PsxDma5)

Bus conventions (verified against MAME traces, see report): little-endian 32-bit bus; a
16-bit handler mapped on that bus sees offset 0 = bytes 0-1 (low half of a 32-bit access) and
offset 1 = bytes 2-3; a 32-bit access is split into two 16-bit handler calls (lo then hi);
a byte access calls the handler once with the byte shifted into its lane and the handler
ignores mem_mask.
"""

BANK_WINDOW_BASE = 0x1FA00000
BANK_WINDOW_SIZE = 0x200000
BANK_REG = 0x1F000000
DMAOFFSET_BASE = 0x1F700000
DMAOFFSET_SIZE = 0x10000
PROT3_ADDR = 0x1F700000          # driver says mirror(0x800000) => 0x1FF00000, but MAME 0.264 does NOT route that to the
                                 # handler (shadowed by the 4 MiB boot ROM at 0x1FC00000); verified by forced test
PROT1_ADDR = 0x1FB00000
PROT2_ADDR = 0x1FB80000

M32 = 0xFFFFFFFF


def _s32(v):
    v &= M32
    return v - (1 << 32) if v & 0x80000000 else v


def _cdiv(a, b):
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


class BankOutOfRange(Exception):
    """MAME memory_bank::set_entry() on an unconfigured entry is a fatalerror."""


class DmaResult:
    __slots__ = ("address", "data", "requested", "words", "is_mainrom", "source_byte_offset",
                 "truncated_rom", "truncated_ram")

    def __init__(self, address, data, requested, words, is_mainrom, source_byte_offset,
                 truncated_rom, truncated_ram):
        self.address = address
        self.data = data
        self.requested = requested
        self.words = words
        self.is_mainrom = is_mainrom
        self.source_byte_offset = source_byte_offset
        self.truncated_rom = truncated_rom
        self.truncated_ram = truncated_ram


class BankedRomController:
    def __init__(self, banked_rom, main_rom=None, ram_size=0x400000, *, alt_bank=False, exp_base=0x1F000000):
        self.banked = bytes(banked_rom)
        self.main = None if main_rom is None else bytes(main_rom)
        self.ram_size = ram_size
        self.alt_bank = alt_bank          # namcos12_altbank_state only; False for tektagt
        self.exp_base = exp_base          # psxcpu exp_base() (value of 0x1f801000); 0x1f300000 selects main ROM DMA
        self.bank_entries = len(self.banked) // 0x200000
        # machine_start()
        self.bank_entry = 0
        self.bankoffset = 0               # m_bankoffset: never written by bankoffset_w (only used if alt_bank)
        self.dmaoffset = 0
        self.tektagdmaoffset = 0
        self.ttt_cnt = 0
        self.ttt_val = [0, 0]             # std::fill_n(m_ttt_val, 0, ..) fills ZERO elements (MAME bug): relies on zeroed storage
        self.has_tektagt_dma = False      # tektagt_state::machine_reset()

    def reset(self):
        """Soft reset: only tektagt_state::machine_reset() (clears m_has_tektagt_dma)."""
        self.has_tektagt_dma = False

    # ------------------------------------------------------------------ handlers
    def bankoffset_w(self, data):
        if data < 0 or data >= self.bank_entries:
            raise BankOutOfRange("bank entry %d >= %d" % (data, self.bank_entries))
        self.bank_entry = data

    def dmaoffset_w(self, offset, data):
        self.dmaoffset = ((offset * 2) | (data << 16)) & M32

    def prot1_w(self, offset, data):
        if offset == 0:
            self.tektagdmaoffset = data & 0xFFFF
            if self.ttt_cnt != 2:
                self.ttt_val[self.ttt_cnt] = data & 0xFFFF
        elif offset == 1:
            self.tektagdmaoffset = (self.tektagdmaoffset | (data << 16)) & M32
            if self.ttt_cnt != 2:
                self.ttt_val[self.ttt_cnt] = (self.ttt_val[self.ttt_cnt] | (data << 16)) & M32
                self.ttt_cnt += 1

    def prot1_r(self, offset):
        return 0x8000 if offset == 0 else 0

    def prot2_w(self, offset, data):
        if offset == 0:
            self.ttt_cnt = 0

    def prot2_r(self, offset):
        if self.ttt_cnt == 2:
            v0, v1 = self.ttt_val
            if v0 == 0x806D2C24 and v1 == 0xD5545715:
                if offset == 0:
                    return 0x36E2
            elif v0 == 0x804C2C84 and v1 == 0xD5545615:
                if offset == 0:
                    return 0x2651
            elif v0 == 0x2AABA8E6 and v1 == 0x00820040:
                if offset == 1:
                    return 0x4186
            elif v0 == 0x2AABA592 and v1 == 0x01780544:
                if offset == 1:
                    return 0x3C7D
            else:
                if ((v1 >> 16) & 0xFF) == 0xA9:
                    return 0x552E
        return 0

    def prot3_r(self):
        self.has_tektagt_dma = True
        return 0

    # ------------------------------------------------------------------ address decode
    def _bank_byte(self, addr):
        return self.banked[self.bank_entry * 0x200000 + (addr - BANK_WINDOW_BASE)]

    def _halfword_handler_read(self, addr):
        """addr is 2-aligned. Returns the 16-bit value or None if not a handler of this controller."""
        if addr == PROT3_ADDR:
            return self.prot3_r()
        if addr == PROT3_ADDR + 2:
            return self.prot3_r()          # map(1f700000,1f700003).r: offset 1 -> handler too (called, flag set)
        if PROT1_ADDR <= addr < PROT1_ADDR + 4:
            return self.prot1_r((addr - PROT1_ADDR) >> 1)
        if PROT2_ADDR <= addr < PROT2_ADDR + 4:
            return self.prot2_r((addr - PROT2_ADDR) >> 1)
        return None

    def handles_read(self, addr):
        a = addr & ~1
        return (BANK_WINDOW_BASE <= addr < BANK_WINDOW_BASE + BANK_WINDOW_SIZE) or \
            (BANK_REG <= addr < BANK_REG + 4) or a in (PROT3_ADDR, PROT3_ADDR + 2)

    def read16(self, addr):
        addr &= ~1
        if BANK_REG <= addr < BANK_REG + 4:
            return 0                       # nopr
        if addr in (PROT3_ADDR, PROT3_ADDR + 2):
            return self.prot3_r()
        v = self._halfword_handler_read(addr)
        if v is not None:
            return v
        if BANK_WINDOW_BASE <= addr < BANK_WINDOW_BASE + BANK_WINDOW_SIZE:
            return self._bank_byte(addr) | (self._bank_byte(addr + 1) << 8)
        return 0                           # unmapped

    def read32(self, addr):
        addr &= ~3
        if BANK_REG <= addr < BANK_REG + 4:
            return 0
        if (PROT1_ADDR <= addr < PROT1_ADDR + 4) or (PROT2_ADDR <= addr < PROT2_ADDR + 4) or \
                addr == PROT3_ADDR:
            lo = self.read16(addr)         # handler offset 0 first, then offset 1
            hi = self.read16(addr + 2)
            return lo | (hi << 16)
        if BANK_WINDOW_BASE <= addr < BANK_WINDOW_BASE + BANK_WINDOW_SIZE:
            return self.read16(addr) | (self.read16(addr + 2) << 16)
        return 0

    def read8(self, addr):
        v = self.read16(addr & ~1)
        return (v >> (8 * (addr & 1))) & 0xFF

    def write16(self, addr, data):
        addr &= ~1
        data &= 0xFFFF
        if addr == BANK_REG:
            self.bankoffset_w(data)
        elif DMAOFFSET_BASE <= addr < DMAOFFSET_BASE + DMAOFFSET_SIZE:
            self.dmaoffset_w((addr - DMAOFFSET_BASE) >> 1, data)
        elif PROT1_ADDR <= addr < PROT1_ADDR + 4:
            self.prot1_w((addr - PROT1_ADDR) >> 1, data)
        elif PROT2_ADDR <= addr < PROT2_ADDR + 4:
            self.prot2_w((addr - PROT2_ADDR) >> 1, data)
        # everything else (bank window, 1f000002, ...) is read-only / unmapped for writes

    def write32(self, addr, data):
        addr &= ~3
        if addr == BANK_REG:
            self.write16(addr, data & 0xFFFF)          # 1f000002.. is not mapped for writes
        elif DMAOFFSET_BASE <= addr < DMAOFFSET_BASE + DMAOFFSET_SIZE or \
                PROT1_ADDR <= addr < PROT1_ADDR + 4 or PROT2_ADDR <= addr < PROT2_ADDR + 4:
            self.write16(addr, data & 0xFFFF)
            self.write16(addr + 2, (data >> 16) & 0xFFFF)

    def write8(self, addr, data):
        self.write16(addr & ~1, (data & 0xFF) << (8 * (addr & 1)))

    # ------------------------------------------------------------------ DMA source (namcos12_rom_read)
    def dma_rom_read(self, address, size):
        """address: RAM byte address (already masked by the PSX DMA code), size: signed word count.
        Consumes m_has_tektagt_dma exactly like the driver. Returns DmaResult (data = bytes to place at RAM[address:])."""
        reg = self.tektagdmaoffset if self.has_tektagt_dma else self.dmaoffset
        res = compute_dma_source(self.banked, self.main, self.ram_size, reg, self.has_tektagt_dma,
                                 self.alt_bank, self.bankoffset, self.exp_base, address, size)
        self.has_tektagt_dma = False
        return res


def compute_dma_source(banked, main, ram_size, offset_register, via_tektagt, alt_bank, bank_offset, exp_base,
                       address, size):
    """Stateless core of namcos12_rom_read(); offset_register is a BYTE offset (bit 31 / exp_base 0x1f300000 -> main ROM)."""
    offset = _s32(offset_register)
    is_mainrom = bool((offset >> 31) & 1) or exp_base == 0x1F300000
    if is_mainrom:
        region = main
        if region is None:
            raise ValueError("main ROM region not supplied")
        offset &= 0xFFFFFF
    else:
        region = banked
        offset &= 0x7FFFFFFF
        if (not via_tektagt) and alt_bank:
            offset += bank_offset * 0x200000
    address = address // 4
    offset = offset // 2                                # halfword index (offset >= 0 here)
    req = size
    truncated_rom = truncated_ram = False
    romleft = _cdiv((len(region) // 2) - offset, 2)       # C++ int division truncates toward zero
    if size > romleft:
        size = romleft
        truncated_rom = True
    ramleft = (ram_size // 4) - address
    if size > ramleft:
        size = ramleft
        truncated_ram = True
    words = max(size, 0)
    start = offset * 2
    data = bytes(region[start:start + words * 4])
    return DmaResult(address * 4, data, req, words, is_mainrom, start, truncated_rom, truncated_ram)


class PsxDma5:
    """psxdma_device (src/devices/cpu/psx/dma.cpp) restricted to channel 5 + DPCR/DICR.
    Channel 5 has only a read handler installed (namcos12_rom_read), no write handler."""

    def __init__(self, ctrl, ram):
        self.ctrl = ctrl
        self.ram = ram                     # bytearray, len == ctrl.ram_size
        self.dpcr = 0
        self.dicr = 0
        self.madr = 0
        self.bcr = 0
        self.chcr = 0
        self.irq_asserted = False
        self.last = None                   # last DmaResult

    def write_dpcr(self, data):
        self.dpcr = data & M32

    def write_madr(self, data):
        self.madr = data & M32

    def write_bcr(self, data):
        self.bcr = data & M32

    def write_dicr(self, data):
        data &= M32
        self.dicr = ((self.dicr & 0x80000000) | (self.dicr & ~data & 0x7F000000) | (data & 0xFF803F)) & M32
        if (self.dicr & 0x80000000) and (self.dicr & 0x7F000000) == 0:
            self.dicr &= ~0x80000000 & M32
            self.irq_asserted = False

    def _finished(self):
        self.chcr &= ~((1 << 0x18) | (1 << 0x1C)) & M32
        self.dicr |= 1 << (24 + 5)
        n_int = (self.dicr >> 24) & 0x7F
        n_mask = (self.dicr >> 16) & 0xFF
        if (n_mask & 0x80) and (n_int & n_mask):
            self.dicr |= 0x80000000
            self.irq_asserted = True
        self.dicr &= (0x00FFFFFF | (self.dicr << 8)) & M32

    def write_chcr(self, data):
        self.chcr = data & M32
        self.last = None
        if (self.chcr & (1 << 0x18)) and (self.dpcr & (1 << (3 + 5 * 4))):
            adrmask = self.ctrl.ram_size - 1
            n_address = self.madr & adrmask
            n_size = _s32(self.bcr)
            if self.chcr & 0x200:
                n_ba = self.bcr >> 16
                if n_ba == 0:
                    n_ba = 0x10000
                n_size = _s32((self.bcr & 0xFFFF) * n_ba)      # unsigned multiply, stored to int32
            if self.chcr == 0x01000000:
                self._run(n_address, n_size)
            elif (self.chcr & 0xFFBFFEFF) == 0x11000000:      # CD-style size calc
                oursize = self.bcr >> 16
                oursize = oursize if oursize > 1 else 1
                oursize = _s32(oursize * (self.bcr & 0xFFFF))
                self._run(n_address, oursize)
            elif self.chcr == 0x01000200:
                self._run(n_address, n_size)
            # every other mode: no fn_write for ch5 -> "unknown mode" (ignored, CHCR stays busy)

    def _run(self, address, size):
        res = self.ctrl.dma_rom_read(address, size)
        self.ram[res.address:res.address + len(res.data)] = res.data
        self.last = res
        self._finished()
