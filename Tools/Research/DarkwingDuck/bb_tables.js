// Resolve bank-5 anim entries for the map bubble anims ($73 plate, $BB completed bubble)
// and print every frame's part table + coordinates, plus whether the part pointer leaves bank 5.
const fs = require('fs');
const root = 'D:/Art/SW Code Zero Unity';
const b5 = fs.readFileSync(root + '/Assets/Darkwing/Data/prg_bank5.bytes');
const B5 = (cpu) => cpu - 0x8000;
const hex = (n) => n.toString(16).toUpperCase().padStart(2, '0');

function entryAddr(anim) {
  return b5[B5(0xb429) + anim] | (b5[B5(0xb4f0) + anim] << 8);
}

function dump(anim) {
  const cpu = entryAddr(anim);
  const e = cpu - 0x8000;
  console.log(`=== anim $${hex(anim)}  entry CPU $${cpu.toString(16).toUpperCase().padStart(4, '0')} (bank5 off 0x${e.toString(16)})`);
  if (e < 0 || e >= b5.length) { console.log('  entry outside bank 5!'); return; }
  const lastRaw = b5[e];
  const last = lastRaw & 0x7f;
  const alt = (lastRaw & 0x80) !== 0;
  console.log(`  entry[0]=$${hex(lastRaw)} lastFrame=${last} table=${alt ? '$9C0B/$9D45' : '$9B0B/$9C45'}  entry[1](duration)=$${hex(b5[e + 1])}`);
  const loT = alt ? 0x9c0b : 0x9b0b, hiT = alt ? 0x9d45 : 0x9c45;
  for (let f = 0; f <= last; f++) {
    const frameId = b5[e + f + 2];
    if (frameId === 0) { console.log(`  frame ${f}: frameId 0 -> end-of-animation (deactivate)`); continue; }
    const partCpu = b5[B5(loT) + frameId] | (b5[B5(hiT) + frameId] << 8);
    const pp = partCpu - 0x8000;
    const inBank = pp >= 0 && pp < 0x4000 - 64;
    let line = `  frame ${f}: frameId=$${hex(frameId)} part=$${partCpu.toString(16).toUpperCase().padStart(4, '0')}`;
    if (!inBank) { console.log(line + '  *** NOT bank 5 (RAM/dynamic template) ***'); continue; }
    const count = b5[pp];
    line += ` parts=${count + 1} coords=$${hex(b5[pp + 1])}`;
    const coordId = b5[pp + 1];
    for (let d = 0; d <= 1; d++) {
      const cid = coordId + d;
      const cCpu = b5[B5(0xae53) + cid] | (b5[B5(0xaf23) + cid] << 8);
      const cp = cCpu - 0x8000 - 2;
      line += ` | dir${d} coord=$${cCpu.toString(16).toUpperCase().padStart(4, '0')}`;
      if (cp < 0 || cp >= 0x4000 - 64) { line += ' (coord NOT bank5)'; continue; }
      let xs = '', ys = [];
      for (let n = 0; n <= count; n++) {
        const t = b5[pp + 2 + n * 2], a = b5[pp + 3 + n * 2];
        const cy = b5[cp + 2 + n * 2], cx = b5[cp + 3 + n * 2];
        ys.push(`${hex(t)}/${hex(a)}/(${String(cx - 0x100 <= -256 ? cx : cx > 127 ? cx - 256 : cx)},${cy > 127 ? cy - 256 : cy})`);
      }
      line += ` [${ys.join(' ')}]`;
    }
    console.log(line);
  }
}

dump(0x73);
dump(0xbb);
dump(0x36);
dump(0x39);
