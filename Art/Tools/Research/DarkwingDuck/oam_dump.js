// Dump active OAM records from a capture's oam_frames.bin for given frames.
const fs = require("fs");
const path = require("path");
const dir = process.argv[2];
const oam = fs.readFileSync(path.join(dir, "oam_frames.bin"));
const FRAME = 256;
const frames = oam.length / FRAME;
console.error("frames=" + frames);
for (const arg of (process.argv[3] || "1").split(",")) {
  const f = Number(arg);
  console.log("=== frame " + f + " ===");
  for (let i = 0; i < FRAME; i += 4) {
    const y = oam[(f - 1) * FRAME + i];
    const tile = oam[(f - 1) * FRAME + i + 1];
    const attr = oam[(f - 1) * FRAME + i + 2];
    const x = oam[(f - 1) * FRAME + i + 3];
    if (y === 0xFF && tile === 0 && attr === 0 && x === 0) continue;
    console.log(
      "  rec " + (i / 4).toString().padStart(2, "0") +
      " Y=$" + y.toString(16).padStart(2, "0") + " (" + y + ")" +
      " T=$" + tile.toString(16).padStart(2, "0") + " (" + tile + ")" +
      " A=$" + attr.toString(16).padStart(2, "0") +
      " X=$" + x.toString(16).padStart(2, "0") + " (" + x + ")");
  }
}
