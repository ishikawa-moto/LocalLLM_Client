"""Explicit provisioning only. Never imported or started by the OCR runtime."""
from pathlib import Path
import argparse,hashlib,json,shutil,subprocess,sys,urllib.request
HERE=Path(__file__).resolve().parent
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def verify(root):
 manifest=json.loads((root/'runtime-manifest.json').read_text(encoding='utf-8'))
 for name,item in manifest['files'].items():
  path=Path(name)
  if path.is_absolute() or '..' in path.parts or not root.joinpath(path).is_file() or sha(root/path)!=item['sha256']:raise ValueError('Asset identity differs')
 return len(manifest['files'])
def main():
 parser=argparse.ArgumentParser();parser.add_argument('--verify-only',type=Path);parser.add_argument('--build',type=Path);parser.add_argument('--provision-network',action='store_true');args=parser.parse_args()
 if args.verify_only:print(json.dumps({'verified_files':verify(args.verify_only.resolve()),'ocr_started':False}));return
 if not args.build or not args.provision_network:raise ValueError('A new destination and explicit --provision-network are required')
 if sys.version_info[:3]!=(3,12,14) or sys.maxsize<2**32:raise ValueError('Use trusted Python3.12.14 x64; no implicit interpreter replacement')
 root=args.build.resolve();root.mkdir(exist_ok=False);base=Path(sys.base_prefix);venv=root/'provision-venv'
 subprocess.run([sys.executable,'-m','venv',str(venv)],check=True)
 subprocess.run([str(venv/'Scripts/python.exe'),'-m','pip','install','--only-binary=:all:','--require-hashes','--no-deps','-r',str(HERE/'requirements-win-py312.lock')],check=True)
 runtime=root/'python';runtime.mkdir()
 for name in ['python.exe','python3.dll','python312.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt']:shutil.copyfile(base/name,runtime/name)
 shutil.copytree(base/'DLLs',runtime/'DLLs')
 shutil.copytree(base/'Lib',runtime/'Lib',ignore=shutil.ignore_patterns('site-packages','test','tests','idlelib','tkinter','turtledemo','ensurepip','__pycache__','*.pyc'))
 shutil.copytree(venv/'Lib/site-packages',runtime/'Lib/site-packages',ignore=shutil.ignore_patterns('pip','pip-*','setuptools','setuptools-*','__pycache__','*.pyc'))
 (runtime/'python312._pth').write_text('.\nLib\nDLLs\nLib/site-packages\n',encoding='utf-8');shutil.copyfile(HERE/'local_ocr_worker.py',root/'local_ocr_worker.py')
 licenses=root/'licenses';licenses.mkdir()
 notices=json.loads((HERE/'licenses/manifest.json').read_text(encoding='utf-8'))
 for name,item in notices['files'].items():
  source=HERE/'licenses'/name
  if Path(name).name!=name or sha(source)!=item['sha256']:raise ValueError('License snapshot identity differs')
  shutil.copyfile(source,licenses/name)
 shutil.copyfile(HERE/'licenses/manifest.json',licenses/'manifest.json')
 for dist in (runtime/'Lib/site-packages').glob('*.dist-info'):
  for p in dist.rglob('*'):
   if p.is_file() and any(t in p.name.lower() for t in ['license','notice','copying']):
    dest=licenses/dist.name/p.relative_to(dist);dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,dest)
 models=root/'models';models.mkdir();pins=json.loads((HERE/'models.lock.json').read_text(encoding='utf-8'))
 for name,item in pins['files'].items():
  if Path(name).name!=name:raise ValueError('Model name')
  with urllib.request.urlopen(item['url'],timeout=90) as response:blob=response.read(item['bytes']+1)
  if len(blob)!=item['bytes'] or hashlib.sha256(blob).hexdigest()!=item['sha256']:raise ValueError('Model download identity differs')
  (models/name).write_bytes(blob)
 (models/'models-manifest.json').write_text(json.dumps({'schema_version':1,'files':pins['files'],'source':pins['source'],'license':pins['license']},indent=2),encoding='utf-8')
 files={p.relative_to(root).as_posix():{'sha256':sha(p),'bytes':p.stat().st_size} for p in [*runtime.rglob('*'),root/'local_ocr_worker.py',*models.rglob('*')] if p.is_file()}
 (root/'runtime-manifest.json').write_text(json.dumps({'schema_version':1,'files':files,'interpreter_qualified':False},indent=2),encoding='utf-8')
 (root/'provision-receipt.json').write_text(json.dumps({'python_version':sys.version,'trusted_base_exe_sha256':sha(base/'python.exe'),'worker_sha256':sha(HERE/'local_ocr_worker.py'),'requirements_sha256':sha(HERE/'requirements-win-py312.lock'),'enabled':False,'requires_actual_offline_resource_priority_regression_qualification':True},indent=2),encoding='utf-8')
 print(json.dumps({'provisioned_files':verify(root),'enabled':False,'ocr_started':False}))
if __name__=='__main__':main()
