from pathlib import Path
import subprocess,json,time,hashlib,argparse,sys
root=next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())

def deliver():
 """Verify an existing build and copy it into a new local output; never rebuild or overwrite."""
 import shutil
 from pathlib import PurePosixPath, PureWindowsPath
 parser=argparse.ArgumentParser(description=deliver.__doc__)
 parser.add_argument('--from',dest='source',type=Path,required=True)
 parser.add_argument('--manifest',type=Path,required=True)
 parser.add_argument('--source-manifest',type=Path,required=True)
 parser.add_argument('--source-project',type=Path,help='Original build project; defaults to this project')
 parser.add_argument('--out',type=Path,required=True)
 parser.add_argument('--evidence',type=Path,required=True)
 args=parser.parse_args(sys.argv[2:])
 def sha(path):
  h=hashlib.sha256()
  with path.open('rb') as stream:
   for block in iter(lambda:stream.read(1024*1024),b''): h.update(block)
  return h.hexdigest()
 def relative(name):
  p=PurePosixPath(name)
  if not name or '\\' in name or ':' in name or p.is_absolute() or PureWindowsPath(name).is_absolute() or '..' in p.parts or p.as_posix()!=name:
   raise ValueError('Unsafe manifest path: '+name)
  return p
 def check_file(base,name,wanted):
  path=base/relative(name)
  if not path.resolve().is_relative_to(base.resolve()) or any(p.is_symlink() or p.is_junction() for p in [path,*path.parents] if p!=p.parent):
   raise ValueError('Linked/outside manifest path: '+name)
  if not path.is_file() or path.stat().st_size!=wanted['bytes'] or sha(path)!=wanted['sha256']:
   raise ValueError('Missing/changed manifest file: '+str(path))
 def inventory(base):
  result=set()
  for p in base.rglob('*'):
   if p.is_symlink() or p.is_junction(): raise ValueError('Linked build path: '+str(p))
   if p.is_file(): result.add(p.relative_to(base).as_posix())
  return result
 source=args.source.absolute()
 output=args.out.absolute()
 evidence=args.evidence.absolute()
 if output.exists(): raise ValueError('Delivery output already exists: '+str(output))
 if not output.resolve().is_relative_to(root.resolve()): raise ValueError('Delivery output must stay in this project')
 if evidence.exists(): raise ValueError('Delivery evidence already exists: '+str(evidence))
 if evidence.resolve().is_relative_to(output.resolve()): raise ValueError('Evidence must stay outside the binary directory')
 manifest=json.loads(args.manifest.read_text(encoding='utf-8-sig'))
 sources=json.loads(args.source_manifest.read_text(encoding='utf-8-sig'))
 if not manifest or not sources: raise ValueError('Empty manifests')
 for name,wanted in manifest.items(): check_file(source,name,wanted)
 if inventory(source)!=set(manifest): raise ValueError('Extra/missing source build files')
 source_project=(args.source_project or root).absolute()
 line_endings=[]
 for name,wanted in sources.items():
  path=source_project/relative(name)
  check_file(source_project,name,{'bytes':path.stat().st_size if path.is_file() else -1,'sha256':wanted})
  local=root/relative(name)
  if not local.is_file(): raise ValueError('Missing local build source: '+name)
  if sha(local)!=wanted:
   before=path.read_bytes(); after=local.read_bytes()
   # Git checkouts may serialize UTF-8 C#/JSON with different line endings. All other changes refuse delivery.
   if local.suffix not in ('.cs','.json') or before.replace(b'\r\n',b'\n')!=after.replace(b'\r\n',b'\n'):
    raise ValueError('Local build source differs: '+name)
   before.decode('utf-8'); after.decode('utf-8')
   line_endings.append({'path':name,'sourceSha256':wanted,'localSha256':sha(local)})
 # All input checks finish before creating output. Partial failures remain visible and are never deleted.
 output.mkdir(parents=True)
 for name in manifest:
  target=output/relative(name)
  target.parent.mkdir(parents=True,exist_ok=True)
  with (source/name).open('rb') as src,target.open('xb') as dst: shutil.copyfileobj(src,dst)
 for name,wanted in manifest.items(): check_file(output,name,wanted)
 if inventory(output)!=set(manifest): raise ValueError('Extra/missing delivered build files')
 result={'schema':1,'scope':'Exact build copy and source-manifest verification; no runtime acceptance',
         'source':str(source),'output':str(output),'sourceProject':str(source_project),'sourceFilesVerified':len(sources),
         'localSourceByteExact':len(sources)-len(line_endings),'localUtf8LineEndingOnly':line_endings,
         'binaryFilesVerified':len(manifest),'binaryBytes':sum(v['bytes'] for v in manifest.values()),
         'binaryManifestSha256':sha(args.manifest),'sourceManifestSha256':sha(args.source_manifest),
         'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
         'verifiedUtcEpoch':time.time(),'result':'PASS'}
 evidence.parent.mkdir(parents=True,exist_ok=True)
 with evidence.open('x',encoding='utf-8') as stream: json.dump(result,stream,ensure_ascii=False,indent=2)
 print(json.dumps(result,ensure_ascii=False))

if len(sys.argv)>1 and sys.argv[1]=='deliver':
 deliver()
 raise SystemExit(0)

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
 for folder in ['Art/Unity/CodeZero','Assets/Scenes','Assets/Settings','ProjectSettings','Packages']:
  files+= [p for p in (root/folder).rglob('*') if p.is_file()]
 return {str(p.relative_to(root)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(set(files))}
source={'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'workingTree':subprocess.check_output(['git','status','--porcelain=v1'],cwd=root,text=True),'sourceHashes':hashes(),'romSha256':hashlib.sha256((root/'Art/Darkwing Duck (USA).nes').read_bytes()).hexdigest()}
(ev/'source_before.json').write_text(json.dumps(source,ensure_ascii=False,indent=2),encoding='utf-8')
r=call('build','--target','StandaloneWindows64','--outputPath',str(exe),'--scenes',json.dumps(['Packages/su.learn.codezero/Scenes/DarkwingMain.unity']),'--confirm','true',*(['--options','["Development"]'] if a.development else []))
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
