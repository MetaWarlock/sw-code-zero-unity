// Full slot-page dump for the city-map phase, used to read off the runtime seed.
// Usage: node map_seed.js <probe> <frame1based>
const fs = require('fs');
const root = 'D:/Art/SW Code Zero Unity';
const probe = process.argv[2] || 'probe_map2';
const f = parseInt(process.argv[3] || '600', 10);
const ram = fs.readFileSync(`${root}/Research/DarkwingDuck/reference/${probe}/ram_frames.bin`);
const b = (f - 1) * 2048;
console.log(`${probe} f${f}  $95=${'$' + ram[b + 0x95].toString(16)} $97=${'$' + ram[b + 0x97].toString(16)} $06=${'$' + ram[b + 0x06].toString(16)} $69=${'$' + ram[b + 0x69].toString(16)}`);
console.log(` scroll: $FA=${ram[b + 0xfa]} $FB=${ram[b + 0xfb]} $FC=$${(ram[b + 0xfc] | (ram[b + 0xfd] << 8)).toString(16)}`);
const pages = [[0x0400, 'fl'], [0x0410, 'fr'], [0x0420, 'an'], [0x0430, 'tm'], [0x0440, 'sy'],
               [0x0450, 'sx'], [0x0480, '480'], [0x04C0, 'hf'], [0x04D0, 'ac'], [0x04E0, 'be'],
               [0x04F0, 'xlo'], [0x0500, 'xhi'], [0x0510, 'ylo'], [0x0520, 'yhi'], [0x0570, 'pr'], [0x0590, 'pl']];
console.log(' slot  ' + pages.map(p => p[1].padStart(4)).join(' '));
for (let s = 0; s < 16; s++)
  console.log(` ${s.toString(16).padStart(2, '0')}   ` + pages.map(([pg, nm]) => '$' + ram[b + pg + s].toString(16).padStart(2, '0').padStart(4)).join(' '));
// OAM records actually present, to confirm the visible object count
const oam = fs.readFileSync(`${root}/Research/DarkwingDuck/reference/${probe}/oam_frames.bin`);
const ob = (f - 1) * 256, cur = ram[b + 0x97];
console.log(` records 1..${cur / 4 - 1}:`);
for (let i = 1; i < cur / 4; i++)
  console.log(`  rec${i.toString(16).padStart(2, '0')}: y=${oam[ob + i * 4].toString(16).padStart(2, '0')} t=${oam[ob + i * 4 + 1].toString(16).padStart(2, '0')} a=${oam[ob + i * 4 + 2].toString(16).padStart(2, '0')} x=${oam[ob + i * 4 + 3].toString(16).padStart(2, '0')}`);
