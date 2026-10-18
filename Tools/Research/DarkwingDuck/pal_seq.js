const fs=require("fs"),path=require("path");
const root="D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/";
for(const name of ["probe_map2","probe_map_nav","probe_map_b1","probe_flow"]){
 const dir=root+name; if(!fs.existsSync(dir+"/ram_frames.bin"))continue;
 const ram=fs.readFileSync(dir+"/ram_frames.bin"),oam=fs.readFileSync(dir+"/oam_frames.bin");
 const F=2048,n=ram.length/F;const hist={};const ex={};
 for(let f=1;f<=n;f++){const b=(f-1)*F;
  const a2=ram[b+0x422],a3=ram[b+0x423],a4=ram[b+0x424];
  if(!((a2===0x73||a2===0xbb)&&(a3===0x73||a3===0xbb)&&(a4===0x73||a4===0xbb)))continue;
  const ps=[];for(let r=0;r<64;r++)if(oam[(f-1)*256+r*4+1]===0xc5)ps.push((oam[(f-1)*256+r*4+2]&3).toString(16));
  const anims=[ram[b+0x422],ram[b+0x423],ram[b+0x424]].map(v=>v.toString(16).toUpperCase()).join("/");
  const k=ps.join(",")+" anims="+anims;
  hist[k]=(hist[k]||0)+1;if(!ex[k])ex[k]=f;}
 console.log("=== "+name);
 for(const k of Object.keys(hist).sort())console.log("  "+k+" -> "+hist[k]+" (first f"+ex[k]+")");
}
