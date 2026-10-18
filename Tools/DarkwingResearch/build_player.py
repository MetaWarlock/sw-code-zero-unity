from pathlib import Path
import subprocess,json,time,hashlib,argparse,sys
root=Path(__file__).resolve().parents[2]
ap=argparse.ArgumentParser(description='Build from the connected Editor; preserve the full BuildReport and source/binary hashes')
ap.add_argument('--out',type=Path,required=True)
ap.add_argument('--evidence',type=Path,required=True)
ap.add_argument('--development',action='store_true')
a=ap.parse_args()
ev=a.evidence.resolve();ev.mkdir(parents=True,exist_ok=True)
exe=a.out.resolve()
cli=str(Path.home()/'AppData/Local/Unity/bin/unity.exe')
base=[cli,'command']; flags=['--project-path',str(root),'--caller','plugin','--skill','unity-cli','--format','json']
commands=[]
def call(name,*args):
 cmd=base+[name,*args]+flags;commands.append(cmd)
 p=subprocess.run(cmd,cwd=root,capture_output=True,timeout=90)
 (ev/f'{name}_{len(commands):03d}.json').write_bytes(p.stdout)
 (ev/'commands.json').write_text(json.dumps(commands,ensure_ascii=False,indent=2),encoding='utf-8')
 r=json.loads(p.stdout.decode('utf-8-sig'))
 if p.returncode or not r.get('success'): raise RuntimeError(r)
 return r['data']['result']
def hashes():
 files=[]
 for folder in ['Assets/Darkwing','Assets/Scenes','Assets/Settings','ProjectSettings','Packages']:
  files+= [p for p in (root/folder).rglob('*') if p.is_file()]
 return {str(p.relative_to(root)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(set(files))}
source={'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'workingTree':subprocess.check_output(['git','status','--porcelain=v1'],cwd=root,text=True),'sourceHashes':hashes(),'romSha256':hashlib.sha256((root/'Art/Darkwing Duck (USA).nes').read_bytes()).hexdigest()}
(ev/'source_before.json').write_text(json.dumps(source,ensure_ascii=False,indent=2),encoding='utf-8')
r=call('build','--target','StandaloneWindows64','--outputPath',str(exe),'--scenes',json.dumps(['Assets/Scenes/DarkwingMain.unity']),'--confirm','true',*(['--options','["Development"]'] if a.development else []))
print(json.dumps(r),flush=True)
end=time.time()+600
while time.time()<end:
 time.sleep(5)
 r=call('build_status')
 if r.get('status') in ('completed','failed'):
  (ev/'build_final.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({k:r.get(k) for k in ['status','buildId','result','totalSizeBytes','buildTimeMs','totalWarnings','totalErrors']}),flush=True);break
else: raise RuntimeError('Build polling deadline')
(ev/'source_after.json').write_text(json.dumps(hashes(),indent=2),encoding='utf-8')
if r.get('result') != 'Succeeded' or r.get('totalErrors') != 0:
 raise RuntimeError('BuildReport failed; existing output is not accepted')
if not exe.is_file(): raise RuntimeError('No standalone executable')
manifest={str(p.relative_to(exe.parent)).replace('\\','/'): {'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in exe.parent.rglob('*') if p.is_file()}
(ev/'binary_manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('Binary manifest written; files',len(manifest),flush=True)
