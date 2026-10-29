const fs=require("fs"),path=require("path");
const root="D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/";
for(const name of ["probe_map2","probe_map_nav","probe_map_b1","probe_flow"]){
 const dir=root+name; if(!fs.existsSync(dir+"/ram_frames.bin"))continue;
 const ram=fs.readFileSync(dir+"/ram_frames.bin"),oam=fs.readFileSync(dir+"/oam_frames.bin");
 const F=2048,n=ram.length/F;
 const counts={};let tot=0,dist={};
 for(let f=1;f<=n;f++){const b=(f-1)*F;
  const a2=ram[b+0x422],a3=ram[b+0x423],a4=ram[b+0x424];
  if(!((a2===0x73||a2===0xbb)&&(a3===0x73||a3===0xbb)&&(a4===0x73||a4===0xbb)))continue;
  let plate=-1,text=-1;
  for(let r=0;r<64;r++){const t=oam[(f-1)*256+r*4+1];
    if(t===0x6e&&plate<0)plate=oam[(f-1)*256+r*4+2]&3;
    if(t===0x5f&&text<0)text=oam[(f-1)*256+r*4+2]&3;}
  if(plate<0||text<0)continue;
  tot++;dist["plate"+plate+"_text"+text]=(dist["plate"+plate+"_text"+text]||0)+1;
  for(let a=0x400;a<0x600;a++){const v=ram[b+a]&3;if(v===plate)counts[a]=(counts[a]||0)+1;}}
 console.log(name+" frames-with-plate="+tot+" dist="+JSON.stringify(dist));
 const hits=Object.entries(counts).filter(([,c])=>c===tot).sort((x,y)=>x[0]-y[0]);
 console.log("  $0400-$05FF bytes equal to plate pal in ALL frames: "+hits.map(([a])=>"$"+(+a).toString(16).toUpperCase()).join(" "));
}
