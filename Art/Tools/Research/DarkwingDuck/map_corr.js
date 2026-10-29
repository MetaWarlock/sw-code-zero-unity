const fs=require("fs"),path=require("path");
const root="D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/";
const keys=["$95&1","$92&1","$92&2","$95&2","frameidx&1","$24&1","$93&1"];
for(const name of ["probe_map2","probe_map_nav","probe_map_b1","probe_flow"]){
 const dir=root+name; if(!fs.existsSync(dir+"/ram_frames.bin"))continue;
 const ram=fs.readFileSync(dir+"/ram_frames.bin"),oam=fs.readFileSync(dir+"/oam_frames.bin");
 const F=2048,n=ram.length/F;let tot=0;const ok={};for(const k of keys)ok[k]=0;
 let unclassified=0;
 for(let f=1;f<=n;f++){const b=(f-1)*F;
  const a2=ram[b+0x422],a3=ram[b+0x423],a4=ram[b+0x424];
  if(!((a2===0x73||a2===0xbb)&&(a3===0x73||a3===0xbb)&&(a4===0x73||a4===0xbb)))continue;
  const t=oam[(f-1)*256+5];let fwd;
  if(t===0x5f)fwd=true;else if(t===0xc5)fwd=false;else{unclassified++;continue;}
  tot++;
  const c95=ram[b+0x95],c92=ram[b+0x92],c24=ram[b+0x24],c93=ram[b+0x93];
  const pred={"$95&1":(c95&1)===1,"$92&1":(c92&1)===1,"$92&2":(c92&2)===0,
    "$95&2":(c95&2)===0,"frameidx&1":(f&1)===1,"$24&1":(c24&1)===1,"$93&1":(c93&1)===1};
  for(const k of keys)if(pred[k]===fwd)ok[k]++;
 }
 console.log(name+": classified="+tot+" unclassified="+unclassified);
 for(const k of keys)console.log("   "+k+" predicts "+ok[k]+"/"+tot);
}
