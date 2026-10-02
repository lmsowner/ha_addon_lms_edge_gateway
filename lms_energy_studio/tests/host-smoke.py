"""Black-box host, access boundary and nested-prefix checks; no real HA credentials."""
import json, os, socket, subprocess, tempfile, time, urllib.request, urllib.error
from pathlib import Path
project = Path(__file__).resolve().parents[1] / 'src/HA.LMS.EnergyStudio'
dll = project / 'bin/Release/net10.0/HA.LMS.EnergyStudio.dll'
def run(environment):
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix='energy-host-test-') as data:
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT=environment, ASPNETCORE_URLS=f'http://127.0.0.1:{port}', EnergyStudio__DataRoot=data, EnergyStudio__PublicPathPrefix='/nested/energy')
        env.pop('SUPERVISOR_TOKEN', None)
        proc = subprocess.Popen(['dotnet', str(dll)], cwd=project, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        base = f'http://127.0.0.1:{port}'
        def request(path, method='GET', body=None, headers=None):
            req = urllib.request.Request(base+path, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers or {})
            try:
                with urllib.request.urlopen(req, timeout=3) as response:
                    return response.status, response.read()
            except urllib.error.HTTPError as e:
                return e.code, e.read()
        try:
            for _ in range(100):
                try:
                    if request('/healthz')[0] == 200: break
                except (OSError, urllib.error.URLError): pass
                time.sleep(.05)
            else: raise AssertionError('Host did not start')
            if environment == 'Production':
                assert request('/api/energy/config')[0] == 401
                assert request('/nested/energy/api/energy/config', headers={'X-Ingress-Path':'/api/hassio_ingress/forged','X-LMS-User':'forged'})[0] == 401
                return
            assert b'Every watt, connected' in request('/nested/energy/')[1]
            assert request('/nested/energy/assets/home.webp')[0] == 200
            assert b'EventSource' in request('/nested/energy/live.js')[1]
            status, body = request('/nested/energy/api/energy/config')
            assert status == 200 and json.loads(body)['revision'] == 0
            with urllib.request.urlopen(base+'/nested/energy/api/energy/live', timeout=3) as response:
                snapshot = json.loads(response.readline().decode().removeprefix('data: '))
                assert snapshot['connection'] == 'disconnected' and snapshot['readings']['home']['value'] is None
            headers={'Content-Type':'application/json','X-Energy-Studio':'1','Origin':base}
            payload={'layout':{},'cells':{},'mappings':{},'expectedRevision':0}
            assert request('/nested/energy/api/energy/config','PUT',payload)[0] == 403
            assert request('/nested/energy/api/energy/config','PUT',{k:v for k,v in payload.items() if k!='expectedRevision'},headers)[0] == 409
            status, body=request('/nested/energy/api/energy/config','PUT',payload,headers)
            assert status == 200 and json.loads(body)['revision'] == 1
            assert request('/nested/energy/api/energy/config','PUT',payload,headers)[0] == 409
            assert request('/nested/energy/api/energy/config','PUT',{**payload,'expectedRevision':1,'layout':{'token':'forbidden'}},headers)[0] == 400
            assert request('/nested/energy/api/energy/config','PUT',{**payload,'expectedRevision':1},{**headers,'Origin':'https://other.invalid'})[0] == 403
        finally:
            proc.terminate()
            try: proc.wait(timeout=5)
            except subprocess.TimeoutExpired: proc.kill(); proc.wait()
run('Development')
run('Production')
print('PASS: nested HTML/assets/API/SSE, persistent revision guard, CSRF, config validation and forged ingress/proxy rejection.')
