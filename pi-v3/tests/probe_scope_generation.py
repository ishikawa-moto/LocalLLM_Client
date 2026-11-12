"""Disposable PC70 WSL probe; scope names alone do not prove ownership."""
import subprocess
import time
import uuid

task=str(uuid.uuid4())
names=[f'lb-v3-{task}-{uuid.uuid4()}.scope' for _ in range(2)]
processes=[]
try:
    for name in names:
        process=subprocess.Popen(['/usr/bin/systemd-run','--user','--scope','--quiet',
                                  '--unit',name,'/usr/bin/sleep','20'],
                                 stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
        processes.append(process)
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        active=[subprocess.run(['/usr/bin/systemctl','--user','is-active','--quiet',name]).returncode == 0 for name in names]
        if all(active):break
        if any(p.poll() is not None for p in processes):raise RuntimeError('fixture scope failed to start')
        time.sleep(.1)
    else:raise RuntimeError('fixture scopes did not become active')
    subprocess.run(['/usr/bin/systemctl','--user','stop','--',names[0]],check=True,timeout=10)
    processes[0].wait(timeout=10)
    if processes[1].poll() is not None:raise RuntimeError('stopping one generation affected another')
    if subprocess.run(['/usr/bin/systemctl','--user','is-active','--quiet',names[1]]).returncode:
        raise RuntimeError('other generation is not active')
    print('DISTINCT_GENERATION_SCOPE_STOP_OBSERVED')
finally:
    for name,p in zip(names,processes):
        if p.poll() is None:
            subprocess.run(['/usr/bin/systemctl','--user','stop','--',name],check=False,timeout=10)
            p.wait(timeout=10)
