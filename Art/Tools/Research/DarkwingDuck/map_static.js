// Which city-map slot fields actually change across the map phase, and how $0410/$0430 evolve.
// Usage: node map_static.js <probe> <firstFrame1based> <lastFrame1based>
const fs = require('fs');
const root = 'D:/Art/SW Code Zero Unity';
const probe = process.argv[2] || 'probe_map2';
const from = parseInt(process.argv[3] || '1', 10);
const to = parseInt(process.argv[4] || '2400', 10);
const ram = fs.readFileSync(`${root}/Research/DarkwingDuck/reference/${probe}/ram_frames.bin`);
const frames = Math.min(to, ram.length / 2048);

// slot pages to profile: flags, anim frame, anim number, timer, screenY, screenX, Y hi/lo, X hi/lo
const pages = [[0x0400, 'fl'], [0x0410, 'fr'], [0x0420, 'an'], [0x0430, 'tm'],
               [0x0440, 'sy'], [0x0450, 'sx'], [0x04F0, 'xlo'], [0x0500, 'xhi'],
               [0x0510, 'ylo'], [0x0520, 'yhi'], [0x04D0, 'ac'], [0x04E0, 'be'],
               [0x0480, '480'], [0x0570, 'pr'], [0x0590, 'pl'], [0x04C0, 'hf']];

console.log(`${probe} f${from}..f${frames}  distinct values per slot page`);
for (let s = 0; s < 6; s++) {
  const parts = [];
  for (const [pg, nm] of pages) {
    const set = new Set();
    for (let f = from; f <= frames; f++) set.add(ram[(f - 1) * 2048 + pg + s]);
    parts.push(`${nm}=${set.size}`);
  }
  console.log(` slot${s}: ${parts.join(' ')}`);
}
for (const [addr, nm] of [[0x95, '$95'], [0x97, '$97'], [0xfa, '$FA'], [0xfb, '$FB'],
                          [0xfc, '$FC'], [0xfd, '$FD'], [0x69, '$69'], [0x06, '$06']]) {
  const set = new Set();
  for (let f = from; f <= frames; f++) set.add(ram[(f - 1) * 2048 + addr]);
  const vals = [...set].map(v => '$' + v.toString(16).padStart(2, '0')).sort();
  console.log(` ${nm}: ${set.size} ${vals.slice(0, 14).join(' ')}${vals.length > 14 ? ' …' : ''}`);
}

// per-slot animation evolution: frameId/timer sequences, and screenY/screenX deltas
console.log('\nslot animation sequences (fr:tm triplets, first 60 map frames):');
for (let s = 0; s < 5; s++) {
  let seq = '';
  for (let f = from; f < from + 60 && f <= frames; f++) {
    const b = (f - 1) * 2048;
    seq += `${ram[b + 0x0410 + s].toString(16).padStart(2, '0')}:${ram[b + 0x0430 + s].toString(16).padStart(2, '0')} `;
  }
  console.log(` slot${s} an=$${ram[(from - 1) * 2048 + 0x0420 + s].toString(16).padStart(2, '0')} ${seq}`);
}
console.log('\nscreen position sequences (sy:sx, first 40 map frames):');
for (let s = 0; s < 5; s++) {
  let seq = '';
  for (let f = from; f < from + 40 && f <= frames; f++) {
    const b = (f - 1) * 2048;
    seq += `${ram[b + 0x0440 + s].toString(16).padStart(2, '0')},${ram[b + 0x0450 + s].toString(16).padStart(2, '0')} `;
  }
  console.log(` slot${s} ${seq}`);
}
