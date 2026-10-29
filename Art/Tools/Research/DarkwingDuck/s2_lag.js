// For probe_map2 frames around the bubble transition: print slot 2's animation fields
// ($0410 frame, $0420 number, $0430 timer), its screen position, and what the ROM actually
// composed at the third 9-record cluster (record 19 = slot 2's first part in a reversed walk).
const fs = require('fs');
const root = 'D:/Art/SW Code Zero Unity';
const dir = process.argv[2] || 'probe_map2';
const ram = fs.readFileSync(`${root}/Research/DarkwingDuck/reference/${dir}/ram_frames.bin`);
const oam = fs.readFileSync(`${root}/Research/DarkwingDuck/reference/${dir}/oam_frames.bin`);
const frames = ram.length / 2048;
const from = +(process.argv[3] || 596), to = +(process.argv[4] || 640);
const hex = (n) => n.toString(16).toUpperCase().padStart(2, '0');

for (let f = from; f <= Math.min(to, frames); f++) {
  const b = (f - 1) * 2048, o = (f - 1) * 256;
  const s = (i) => `s${i}: fr=${hex(ram[b + 0x0410 + i])} an=${hex(ram[b + 0x0420 + i])} tm=${hex(ram[b + 0x0430 + i])} y=${hex(ram[b + 0x0440 + i])} x=${hex(ram[b + 0x0450 + i])} fl=${hex(ram[b + 0x0400 + i])}`;
  const rec = (r) => `r${r}=${hex(oam[o + r * 4])}/${hex(oam[o + r * 4 + 1])}/${hex(oam[o + r * 4 + 2])}/${hex(oam[o + r * 4 + 3])}`;
  console.log(`f${f} $95=${hex(ram[b + 0x95])} $97=${hex(ram[b + 0x97])} | ${s(2)} | ${s(3)} | ${s(4)} | rom ${rec(19)} ${rec(20)} ${rec(27)}`);
}
