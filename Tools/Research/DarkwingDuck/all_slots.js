const fs=require("fs"),path=require("path");
const dir=process.argv[2];const ram=fs.readFileSync(path.join(dir,"ram_frames.bin"));
const F=2048;const at=(f,a)=>ram[(f-1)*F+a];const h=v=>"$"+v.toString(16).toUpperCase().padStart(2,"0");
const flds=[["Fl",0x0400],["AF",0x0410],["AN",0x0420],["AT",0x0430],["SY",0x0440],["SX",0x0450],["T480",0x0480],["Act",0x04D0],["Beh",0x04E0],["Xlo",0x04F0],["Xhi",0x0500],["Ylo",0x0510],["Yhi",0x0520],["Pm",0x0570],["Dir",0x0580],["Hp",0x05A0]];
for(const arg of (process.argv[3]||"1").split(",")){const f=Number(arg);
console.log("--- frame "+f+"  $6C="+h(at(f,0x6C))+" $69="+h(at(f,0x69))+" $38="+h(at(f,0x38))+" $62="+h(at(f,0x62))+" $97="+h(at(f,0x97)));
for(let i=0;i<16;i++){const act=at(f,0x04D0+i);if(act===0&&at(f,0x0420+i)===0&&at(f,0x0450+i)===0)continue;
console.log("  s"+i+" "+flds.map(([n,a])=>n+":"+h(at(f,a+i))).join(" "));}}
