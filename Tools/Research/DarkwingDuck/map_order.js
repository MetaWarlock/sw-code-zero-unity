const fs=require("fs"),path=require("path");
const dir=process.argv[2];
const ram=fs.readFileSync(path.join(dir,"ram_frames.bin")),oam=fs.readFileSync(path.join(dir,"oam_frames.bin"));
const F=2048,n=ram.length/F;
const hist={};
let other=[];
for(let f=1;f<=n;f++){const b=(f-1)*F;
  const a2=ram[b+0x0422],a3=ram[b+0x0423],a4=ram[b+0x0424];
  if(!(a2===0x73||a2===0xbb)||!(a3===0x73||a3===0xbb)||!(a4===0x73||a4===0xbb))continue;
  const t=oam[(f-1)*256+5];
  const order=t===0x5f?"fwd":t===0xc5?"rev":"other$"+t.toString(16);
  const k=order+" p95bit0="+((ram[b+0x95]&1))+" p95="+ram[b+0x95].toString(16).toUpperCase();
  hist[k]=(hist[k]||0)+1;
  if(order.startsWith("other"))other.push(f+":"+k);}
for(const k of Object.keys(hist).sort())console.log(k,"->",hist[k]);
console.log("other frames:",other.slice(0,10).join(" "));
