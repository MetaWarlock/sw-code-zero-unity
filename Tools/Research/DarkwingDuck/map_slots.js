// Dump map-phase slot state from probe_flow/ram_frames.bin (2400 frames x $0800).
// Slot arrays are indexed: $0400+i Flags, $0410+i AnimFrame, $0420+i AnimNumber,
// $0430+i AnimTimer, $0440+i ScreenY, $0450+i ScreenX, $04D0+i Active/StateLow,
// $04E0+i Behavior, $0570+i BehaviorParam.
const fs = require("fs");
const path = require("path");
const dir = process.argv[2] ||
  "D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/probe_flow";
const ram = fs.readFileSync(path.join(dir, "ram_frames.bin"));
const FRAME = 2048;
const frames = ram.length / FRAME;
const at = (f, a) => ram[(f - 1) * FRAME + a];
const hex = (v) => "$" + v.toString(16).padStart(2, "0").toUpperCase();

const fields = [
  ["Flags", 0x0400], ["AnimFrame", 0x0410], ["AnimNumber", 0x0420],
  ["AnimTimer", 0x0430], ["ScreenY", 0x0440], ["ScreenX", 0x0450],
  ["T480", 0x0480], ["T490", 0x0490], ["T4A0", 0x04A0], ["T4B0", 0x04B0],
  ["Active", 0x04D0], ["Behavior", 0x04E0], ["Xlo", 0x04F0], ["Xhi", 0x0500],
  ["Ylo", 0x0510], ["Yhi", 0x0520], ["Param", 0x0570], ["Dir", 0x0580],
  ["Health", 0x05A0], ["Place", 0x0590],
];

function dump(f) {
  const zp = ["$6C", "$69", "$38", "$31", "$62", "$92", "$95", "$97", "$6A", "$6B"]
    .map((n) => {
      const a = parseInt(n.slice(1), 16);
      return n + "=" + hex(at(f, a));
    })
    .join(" ");
  console.log("frame " + f + "  " + zp);
  for (let i = 1; i <= 6; i++) {
    const row = fields.map(([n, a]) => n + ":" + hex(at(f, a + i))).join(" ");
    console.log("  slot " + i + " " + row);
  }
}

if (process.argv[3] === "scan") {
  // Report frames where any of slots 1..6 has AnimNumber $73/$BB/$BD (map bubble family).
  let prev = null;
  for (let f = 1; f <= frames; f++) {
    const sig = [1, 2, 3, 4, 5].map((i) => at(f, 0x0420 + i).toString(16)).join(",");
    if (sig !== prev) {
      console.log("f" + f + " $6C=" + hex(at(f, 0x6C)) + " anims[" + sig + "]");
      prev = sig;
    }
  }
} else {
  const list = (process.argv[3] || "2400").split(",").map(Number);
  for (const f of list) dump(f);
}
