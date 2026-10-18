const fs=require("fs"),path=require("path");
const dir=process.argv[2],lo=+process.argv[3],hi=+process.argv[4];
const ram=fs.readFileSync(path.join(dir,"ram_frames.bin")),oam=fs.readFileSync(path.join(dir,"oam_frames.bin"));
const F=2048;
for(let f=lo;f<=hi;f++){const b=(f-1)*F;
 const a2=ram[b+0x422],a3=ram[b+0x423],a4=ram[b+0x424];
 const isMap=(a2===0x73||a2===0xbb)&&(a3===0x73||a3===0xbb)&&(a4===0x73||a4===0xbb);
 if(!isMap)continue;
 const t=oam[(f-1)*256+5];
 const order=t===0x5f?"FWD":t===0xc5?"REV":"ovr"+t.toString(16).toUpperCase();
 let act=0;for(let i=0;i<16;i++)if(ram[b+0x4D0+i]&0x80)act++;
 let anims="";for(let i=0;i<6;i++)anims+=ram[b+0x420+i].toString(16).toUpperCase()+" ";
 console.log("f"+f+" $95="+ram[b+0x95].toString(16).toUpperCase().padStart(2,"0")+
  " bit0="+(ram[b+0x95]&1)+" ord="+order+" $97="+ram[b+0x97].toString(16).toUpperCase()+
  " act="+act+" anims="+anims.trim());}
