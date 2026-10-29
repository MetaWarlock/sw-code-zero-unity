"""6502 disassembler for Darkwing Duck research with MMC1 bank mapping and CDL guidance.

CPU address -> file offset depends on the MMC1 mapping at the time of execution.
This tool disassembles a PRG bank as if mapped to a window, so offsets are unambiguous.
Outputs an annotated listing. CDL bytes (Mesen NesPrgRom): per byte flags
bit0=code(executed), bit1=data(read), bit2=... (see Mesen CDL format: 0=unused,
1=code, 2=data...). We treat any byte flagged code as an instruction start candidate.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())

# opcode table: mnemonic, addressing mode
MODES = {
    "imp": "imp", "acc": "acc", "imm": "imm", "zp": "zp", "zpx": "zpx", "zpy": "zpy",
    "izx": "izx", "izy": "izy", "iz0": "iz0", "abs": "abs", "abx": "abx", "aby": "aby",
    "ind": "ind", "rel": "rel",
}
SIZE = {"imp": 1, "acc": 1, "imm": 2, "zp": 2, "zpx": 2, "zpy": 2, "izx": 2, "izy": 2,
        "iz0": 2, "abs": 3, "abx": 3, "aby": 3, "ind": 3, "rel": 2}

# Official 6502 opcodes: opcode -> (mnemonic, mode)
OPCODES = {
0x00:("BRK","imp"),0x01:("ORA","izx"),0x05:("ORA","zp"),0x06:("ASL","zp"),0x08:("PHP","imp"),
0x09:("ORA","imm"),0x0A:("ASL","acc"),0x0D:("ORA","abs"),0x0E:("ASL","abs"),
0x10:("BPL","rel"),0x11:("ORA","izy"),0x15:("ORA","zpx"),0x16:("ASL","zpx"),0x18:("CLC","imp"),
0x19:("ORA","aby"),0x1D:("ORA","abx"),0x1E:("ASL","abx"),
0x20:("JSR","abs"),0x21:("AND","izx"),0x24:("BIT","zp"),0x25:("AND","zp"),0x26:("ROL","zp"),
0x28:("PLP","imp"),0x29:("AND","imm"),0x2A:("ROL","acc"),0x2C:("BIT","abs"),0x2D:("AND","abs"),
0x2E:("ROL","abs"),0x30:("BMI","rel"),0x31:("AND","izy"),0x35:("AND","zpx"),0x36:("ROL","zpx"),
0x38:("SEC","imp"),0x39:("AND","aby"),0x3D:("AND","abx"),0x3E:("ROL","abx"),
0x40:("RTI","imp"),0x41:("EOR","izx"),0x45:("EOR","zp"),0x46:("LSR","zp"),0x48:("PHA","imp"),
0x49:("EOR","imm"),0x4A:("LSR","acc"),0x4C:("JMP","abs"),0x4D:("EOR","abs"),0x4E:("LSR","abs"),
0x50:("BVC","rel"),0x51:("EOR","izy"),0x55:("EOR","zpx"),0x56:("LSR","zpx"),0x58:("CLI","imp"),
0x59:("EOR","aby"),0x5D:("EOR","abx"),0x5E:("LSR","abx"),
0x60:("RTS","imp"),0x61:("ADC","izx"),0x65:("ADC","zp"),0x66:("ROR","zp"),0x68:("PLA","imp"),
0x69:("ADC","imm"),0x6A:("ROR","acc"),0x6C:("JMP","ind"),0x6D:("ADC","abs"),0x6E:("ROR","abs"),
0x70:("BVS","rel"),0x71:("ADC","izy"),0x75:("ADC","zpx"),0x76:("ROR","zpx"),0x78:("SEI","imp"),
0x79:("ADC","aby"),0x7D:("ADC","abx"),0x7E:("ROR","abx"),
0x81:("STA","izx"),0x84:("STY","zp"),0x85:("STA","zp"),0x86:("STX","zp"),0x88:("DEY","imp"),
0x8A:("TXA","imp"),0x8C:("STY","abs"),0x8D:("STA","abs"),0x8E:("STX","abs"),
0x90:("BCC","rel"),0x91:("STA","izy"),0x94:("STY","zpx"),0x95:("STA","zpx"),0x96:("STX","zpy"),
0x98:("TYA","imp"),0x99:("STA","aby"),0x9A:("TXS","imp"),0x9D:("STA","abx"),
0xA0:("LDY","imm"),0xA1:("LDA","izx"),0xA2:("LDX","imm"),0xA4:("LDY","zp"),0xA5:("LDA","zp"),
0xA6:("LDX","zp"),0xA8:("TAY","imp"),0xA9:("LDA","imm"),0xAA:("TAX","imp"),0xAC:("LDY","abs"),
0xAD:("LDA","abs"),0xAE:("LDX","abs"),0xB0:("BCS","rel"),0xB1:("LDA","izy"),0xB4:("LDY","zpx"),
0xB5:("LDA","zpx"),0xB6:("LDX","zpy"),0xB8:("CLV","imp"),0xB9:("LDA","aby"),0xBA:("TSX","imp"),
0xBC:("LDY","abx"),0xBD:("LDA","abx"),0xBE:("LDX","aby"),
0xC0:("CPY","imm"),0xC1:("CMP","izx"),0xC4:("CPY","zp"),0xC5:("CMP","zp"),0xC6:("DEC","zp"),
0xC8:("INY","imp"),0xC9:("CMP","imm"),0xCA:("DEX","imp"),0xCC:("CPY","abs"),0xCD:("CMP","abs"),
0xCE:("DEC","abs"),0xD0:("BNE","rel"),0xD1:("CMP","izy"),0xD5:("CMP","zpx"),0xD6:("DEC","zpx"),
0xD8:("CLD","imp"),0xD9:("CMP","aby"),0xDD:("CMP","abx"),0xDE:("DEC","abx"),
0xE0:("CPX","imm"),0xE1:("SBC","izx"),0xE4:("CPX","zp"),0xE5:("SBC","zp"),0xE6:("INC","zp"),
0xE8:("INX","imp"),0xE9:("SBC","imm"),0xEA:("NOP","imp"),0xEC:("CPX","abs"),0xED:("SBC","abs"),
0xEE:("INC","abs"),0xF0:("BEQ","rel"),0xF1:("SBC","izy"),0xF5:("SBC","zpx"),0xF6:("INC","zpx"),
0xF8:("SED","imp"),0xF9:("SBC","aby"),0xFD:("SBC","abx"),0xFE:("INC","abx"),
}


def load_labels() -> dict[int, str]:
    path = ROOT / "Art/Research/DarkwingDuck/labels.json"
    if path.exists():
        data = json.loads(path.read_text(encoding="utf-8"))
        return {int(k, 16): v for k, v in data.items()}
    return {}


def disassemble_bank(prg: bytes, bank: int, cpu_base: int, cdl: bytes | None,
                     labels: dict[int, str], start: int | None, length: int) -> str:
    """cpu_base: CPU address where this bank's first byte sits when mapped."""
    out = []
    base = bank * 0x4000
    if start is None:
        start = 0
    end = min(start + length, len(prg) - base)
    i = start
    while i < end:
        cpu = cpu_base + i
        off = base + i
        op = prg[off]
        if op in OPCODES:
            mn, mode = OPCODES[op]
            size = SIZE[mode]
            if i + size > end:
                break
            operands = prg[off + 1: off + size]
            cdl_note = ""
            if cdl is not None and off < len(cdl):
                f = cdl[off]
                cdl_note = {0: ".", 1: "C", 2: "D", 3: "CD", 4: "J", 5: "J", 6: "DJ", 7: "CDJ",
                            8: "S"}.get(f, f"?{f}")
            if mode == "imp":
                text = mn
            elif mode == "acc":
                text = f"{mn} A"
            elif mode == "imm":
                text = f"{mn} #${operands[0]:02X}"
            elif mode == "zp":
                text = f"{mn} ${operands[0]:02X}"
            elif mode == "zpx":
                text = f"{mn} ${operands[0]:02X},X"
            elif mode == "zpy":
                text = f"{mn} ${operands[0]:02X},Y"
            elif mode == "izx":
                text = f"{mn} (${operands[0]:02X},X)"
            elif mode == "izy":
                text = f"{mn} (${operands[0]:02X}),Y"
            elif mode == "iz0":
                text = f"{mn} (${operands[0]:02X})"
            elif mode == "abs":
                target = operands[0] | (operands[1] << 8)
                lbl = labels.get(target, "")
                text = f"{mn} ${target:04X}" + (f" ; {lbl}" if lbl else "")
            elif mode == "abx":
                target = operands[0] | (operands[1] << 8)
                lbl = labels.get(target, "")
                text = f"{mn} ${target:04X},X" + (f" ; {lbl}" if lbl else "")
            elif mode == "aby":
                target = operands[0] | (operands[1] << 8)
                lbl = labels.get(target, "")
                text = f"{mn} ${target:04X},Y" + (f" ; {lbl}" if lbl else "")
            elif mode == "ind":
                target = operands[0] | (operands[1] << 8)
                text = f"{mn} (${target:04X})"
            elif mode == "rel":
                delta = operands[0] - 256 if operands[0] >= 128 else operands[0]
                target = cpu + 2 + delta
                lbl = labels.get(target, "")
                text = f"{mn} ${target:04X}" + (f" ; {lbl}" if lbl else "")
            raw = prg[off:off + size].hex(" ").upper()
            out.append(f"{cpu:04X}  {raw:<9} {text:<28} ;{cdl_note}")
            i += size
        else:
            out.append(f"{cpu:04X}  {op:02X}        ???")
            i += 1
    return "\n".join(out)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--bank", type=int, required=True, help="PRG bank index 0-7")
    p.add_argument("--cpu-base", type=lambda x: int(x, 0), default=0x8000)
    p.add_argument("--cdl", type=Path, help="Mesen NesPrgRom CDL file from a run")
    p.add_argument("--start", type=lambda x: int(x, 0), default=None, help="bank offset start")
    p.add_argument("--length", type=lambda x: int(x, 0), default=0x4000)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--cdl-only", action="store_true", help="only emit bytes flagged as code in CDL")
    args = p.parse_args()
    prg = (ROOT / "Art/Research/DarkwingDuck/extracted/raw/prg.bin").read_bytes()
    cdl = args.cdl.read_bytes() if args.cdl else None
    if cdl is not None:
        # CDL covers PRG in bank order (16 banks of 4K for 128K PRG)
        off0 = args.bank * 0x4000
        cdl = cdl[off0: off0 + 0x4000]
    labels = load_labels()
    text = disassemble_bank(prg, args.bank, args.cpu_base, cdl, labels, args.start, args.length)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(text, encoding="utf-8")
    print(f"wrote {args.out} ({len(text.splitlines())} lines)")


if __name__ == "__main__":
    main()
