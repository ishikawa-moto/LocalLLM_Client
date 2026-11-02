"""Pi 1.4 startup only: existing provider/Host tools, no model or Windows binary.

usage: python3 test_existing_rpc_startup.py /absolute/node /absolute/pi-runtime
The disposable directory is retained as evidence. Does not install a runtime.
"""
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import sys
import tempfile
import time

node, runtime = (Path(value).resolve(strict=True) for value in sys.argv[1:])
package = runtime / 'node_modules/@earendil-works/pi-coding-agent'
assert json.loads((package / 'package.json').read_text())['version'] == '1.4.0'
client = Path(__file__).resolve().parents[2]
root = Path(tempfile.mkdtemp(prefix='existing-rpc-', dir=runtime.parent))
root.chmod(0o700)
extensions = root / 'extensions'
extensions.mkdir()
(extensions / 'node_modules').symlink_to(runtime / 'node_modules', target_is_directory=True)
for name in ('localbrain-provider.js', 'localbrain-policy.mjs', 'host-file-tools.mjs', 'host-request.mjs', 'context-telemetry.mjs'):
    shutil.copyfile(client / 'scripts' / name, extensions / name)
agent, work = root / 'agent', root / 'work'
agent.mkdir(); work.mkdir()
shutil.copyfile(client / 'pi-v3/agent/localbrain-settings.json', agent / 'settings.json')
probe = extensions / 'startup-probe.mjs'
probe.write_text('''import fs from 'node:fs';
export default pi => pi.on('session_start', () => fs.writeFileSync(process.env.PROBE_RESULT,
 JSON.stringify({tools:pi.getActiveTools(),settings:pi.getSettings()})));
''')
discovery = work / '.pi/extensions'
discovery.mkdir(parents=True)
(discovery / 'must-not-load.mjs').write_text("throw new Error('PROJECT_EXTENSION_MUST_NOT_RUN');")
(work / '.pi/settings.json').write_text('{"compaction":{"enabled":false}}')
args = [str(node), str(package / 'dist/bundle/cli.js'), '--mode', 'rpc', '--offline', '--no-approve',
        '--no-extensions', '--no-mcp', '--no-skills', '--no-prompt-templates', '--no-themes', '--no-context-files',
        '--provider', 'localbrain', '--model', 'local-qwen38', '--session-dir', str(agent / 'sessions'),
        '--tools', 'read,ls,host_recall,host_memory_search']
for name in ('localbrain-provider.js', 'localbrain-policy.mjs', 'host-file-tools.mjs', 'startup-probe.mjs'):
    args += ['--extension', str(extensions / name)]
env = dict(os.environ, PI_CODING_AGENT_DIR=str(agent), PROBE_RESULT=str(root / 'probe.json'))
# No prompt, no task data, no credentials, no relay. Provider startup only.
for key in list(env):
    if key.startswith(('LOCALBRAIN_', 'OPENAI_', 'ANTHROPIC_')):
        env.pop(key)
with (root / 'stderr.txt').open('wb') as errors:
    process = subprocess.Popen(args, cwd=work, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
    try:
        process.stdin.write(b'{"id":"startup","type":"get_state"}\n'); process.stdin.flush()
        deadline = time.monotonic() + 35
        state = None
        while time.monotonic() < deadline:
            if not select.select([process.stdout], [], [], 1)[0]:
                continue
            frame = process.stdout.readline(1024 * 1024)
            if not frame: raise RuntimeError('Pi exited before get_state; inspect retained stderr')
            response = json.loads(frame)
            if response.get('id') == 'startup':
                assert response['success'] is True
                state = response['data']; break
        assert state is not None, 'startup timeout'
        assert state['model']['id'] == 'local-qwen38'
        assert state['autoCompactionEnabled'] is True
        assert Path(state['sessionFile']).parent == agent / 'sessions'
        observed = json.loads((root / 'probe.json').read_text())
        assert set(observed['tools']) == {'read', 'ls', 'host_recall', 'host_memory_search'}
        assert observed['settings']['compaction'] == {'enabled': True, 'reserveTokens': 8192, 'keepRecentTokens': 12000}
        process.stdin.close(); process.wait(timeout=10)
        assert process.returncode == 0
        (root / 'result.json').write_text(json.dumps({'startup': 'passed', 'inference_calls': 0, 'windows_host_execution': 'not_run'}))
        print(json.dumps({'result': 'passed', 'evidence': str(root), 'inference_calls': 0}))
    finally:
        if process.poll() is None:
            process.kill(); process.wait(timeout=5)
