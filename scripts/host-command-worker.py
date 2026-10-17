"""Owned WSL command supervisor, standard-library only. No model or network calls.

The Windows Host owns a short-lived lease. Losing it terminates this command's
process group. Trusted personal code is assumed; daemon escape is not sandboxed.
"""
import hashlib, json, os, pathlib, selectors, signal, subprocess, sys, time

def atomic(path, value):
    temp=path.with_suffix(path.suffix+'.pending')
    with temp.open('x',encoding='utf-8') as f:
        json.dump(value,f);f.flush();os.fsync(f.fileno())
    os.replace(temp,path)

def main():
    plan_path=pathlib.Path(sys.argv[1]);raw=plan_path.read_bytes();plan=json.loads(raw)
    identity=hashlib.sha256(raw).hexdigest();receipt=pathlib.Path(plan['receipt'])
    if receipt.exists(): raise RuntimeError('Existing terminal receipt; do not replay')
    for p in (plan['stdout'],plan['stderr']):
        if pathlib.Path(p).exists(): raise RuntimeError('Existing command output; inspect prior attempt')
    lease=pathlib.Path(plan['lease']);deadline=time.monotonic()+plan['timeout_seconds'];limit=plan['max_output_bytes']
    started=time.time();outcome='unknown';child=None;selector=selectors.DefaultSelector();streams={};total=0
    def terminate():
        if child:
            try: os.killpg(child.pid,signal.SIGTERM)
            except ProcessLookupError: pass
    try:
        current=json.loads(lease.read_text(encoding='utf-8-sig'))
        if current['nonce']!=plan['nonce'] or current['expires_at']<=time.time(): raise RuntimeError('Lease unavailable before launch')
        child=subprocess.Popen(['/bin/bash','-lc','exec "$@"','localbrain-command',plan['program'],*plan['arguments']],
            cwd=plan['cwd'],stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True)
        atomic(pathlib.Path(plan['started']),dict(plan_sha256=identity,nonce=plan['nonce'],pid=child.pid,started_at=started))
        for name,pipe in [('stdout',child.stdout),('stderr',child.stderr)]:
            os.set_blocking(pipe.fileno(),False);selector.register(pipe,selectors.EVENT_READ,name)
            streams[name]=open(plan[name],'xb')
        while selector.get_map():
            if time.monotonic()>=deadline: outcome='timeout';break
            try:
                current=json.loads(lease.read_text(encoding='utf-8-sig'))
                if current['nonce']!=plan['nonce'] or current['expires_at']<=time.time(): outcome='owner_lost';break
                if current.get('cancel'): outcome='cancelled';break
            except (OSError,ValueError,KeyError): outcome='owner_lost';break
            for key,_ in selector.select(.2):
                data=os.read(key.fileobj.fileno(),8192)
                if not data: selector.unregister(key.fileobj);continue
                remaining=limit-total;streams[key.data].write(data[:max(0,remaining)]);total+=len(data)
                if total>limit: outcome='output_limit';break
            if outcome=='output_limit': break
        # A child can close its pipes and keep running; the same deadline/lease still applies.
        while outcome=='unknown' and child.poll() is None:
            if time.monotonic()>=deadline: outcome='timeout';break
            try:
                current=json.loads(lease.read_text(encoding='utf-8-sig'))
                if current['nonce']!=plan['nonce'] or current['expires_at']<=time.time(): outcome='owner_lost';break
                if current.get('cancel'): outcome='cancelled';break
            except (OSError,ValueError,KeyError): outcome='owner_lost';break
            time.sleep(.2)
        if outcome=='unknown': outcome='completed'
    finally:
        terminate()
        if child:
            try: child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                try: os.killpg(child.pid,signal.SIGKILL)
                except ProcessLookupError: pass
                child.wait(timeout=3)
        for f in streams.values(): f.close()
        selector.close()
        atomic(receipt,dict(plan_sha256=identity,nonce=plan['nonce'],pid=child.pid if child else None,
            state=outcome,exit_code=child.returncode if child else None,started_at=started,finished_at=time.time(),
            stdout_sha256=hashlib.sha256(pathlib.Path(plan['stdout']).read_bytes()).hexdigest() if pathlib.Path(plan['stdout']).exists() else None,
            stderr_sha256=hashlib.sha256(pathlib.Path(plan['stderr']).read_bytes()).hexdigest() if pathlib.Path(plan['stderr']).exists() else None))

if __name__=='__main__': main()
