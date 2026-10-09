"""Windows Host operator entry: explicit local PDF -> unverified canonical reference."""
import argparse,hashlib,json,os,pathlib,subprocess,sys
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()

def main():
    ap=argparse.ArgumentParser(description='Import a local PDF as an unverified reference; no model or knowledge adoption.')
    ap.add_argument('--pdf',type=pathlib.Path,required=True);ap.add_argument('--workspace',type=pathlib.Path,required=True);ap.add_argument('--source-uri',required=True);ap.add_argument('--retrieved-at',required=True);ap.add_argument('--settings',type=pathlib.Path,required=True)
    a=ap.parse_args();cfg=json.loads(a.settings.read_bytes());package=a.settings.resolve().parent
    for f,h in cfg['tool_files'].items():
        p=(package/f).resolve()
        if not p.is_relative_to(package) or sha(p)!=h:raise ValueError('Installed operator source differs; inspect before reuse')
    from reference_corpus import persist,validate
    from reference_pdf_prepare import MAX_BYTES
    exe=pathlib.Path(cfg['host_exe']);dll=exe.parent/'localbrain.dll';config=pathlib.Path(cfg['client_config'])
    if sha(exe)!=cfg['host_exe_sha256'] or sha(dll)!=cfg['host_dll_sha256'] or sha(config)!=cfg['client_config_sha256']:raise ValueError('Host runtime or config changed; rebind through reviewed installation')
    pdf=a.pdf.resolve();workspace=a.workspace.resolve()
    if not workspace.is_dir() or not pdf.is_file() or pdf.is_symlink():raise ValueError('Explicit local PDF and workspace required')
    with pdf.open('rb') as f:raw=f.read(MAX_BYTES+1)
    if not 0<len(raw)<=MAX_BYTES:raise ValueError('PDF exceeds 4MiB input budget')
    corpus=pathlib.Path(cfg['prepared_corpus']).resolve()
    prepared=persist(corpus,raw,a.source_uri,a.retrieved_at);bundle=pathlib.Path(prepared['path']);manifest=validate(bundle)
    env=os.environ.copy();env['LOCALBRAIN_CLIENT_CONFIG']=str(config)
    process=subprocess.run([str(exe),'agent-v2','reference-import',str(workspace),str(bundle)],env=env,stdin=subprocess.DEVNULL,capture_output=True,timeout=90,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    if process.returncode:raise ValueError('Host rejected reference import; prepared immutable bundle retained')
    result=json.loads(process.stdout)
    if result['knowledgeAdopted'] is not False:raise ValueError('Unexpected Host adoption result')
    if manifest['claims_are_verified'] is not False or manifest['allow_fact_promotion'] is not False:raise ValueError('Reference truth flags differ')
    print(json.dumps({'status':'IMPORTED_UNVERIFIED_REFERENCE','bundle':str(bundle),'prepared_duplicate':prepared['duplicate'],'host':result,'knowledge_adopted':False,'network_fetch_performed':False,'model_calls':0},ensure_ascii=False))
if __name__=='__main__':
    try:main()
    except Exception as e:
        # Print validation failures, never Host stderr/config/TLS/source body.
        print(str(e) if isinstance(e,ValueError) else type(e).__name__,file=sys.stderr);sys.exit(1)