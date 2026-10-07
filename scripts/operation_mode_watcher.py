#!/usr/bin/env python3
from __future__ import annotations
import json, os, pathlib, subprocess, time
from datetime import datetime, timezone

ROOT=pathlib.Path(__file__).resolve().parents[1]
CONTROL=ROOT/'data'/'operation-mode-control'
REQUEST=CONTROL/'request.json'
STATUS=CONTROL/'status.json'
SWITCH=ROOT/'scripts'/'switch_operation_mode.py'
POLL_SECONDS=0.25


def now(): return datetime.now(timezone.utc).astimezone().isoformat()

def atomic(path,payload):
    path.parent.mkdir(parents=True,exist_ok=True)
    tmp=path.with_suffix(path.suffix+'.tmp')
    tmp.write_text(json.dumps(payload,ensure_ascii=False,indent=2),encoding='utf-8')
    os.replace(tmp,path)

def read_json(path):
    try:return json.loads(path.read_text(encoding='utf-8'))
    except Exception:return None

def main():
    CONTROL.mkdir(parents=True,exist_ok=True)
    last_id=None
    print(f'A18 operation-mode watcher active: {CONTROL}',flush=True)
    while True:
        req=read_json(REQUEST)
        if not req or req.get('requestId')==last_id:
            time.sleep(POLL_SECONDS); continue
        request_id=str(req.get('requestId') or '')
        target=str(req.get('targetMode') or '')
        if not request_id:
            time.sleep(POLL_SECONDS); continue
        last_id=request_id
        mode='legacy' if target.lower()=='legacy' else 'fullmarket' if target.lower()=='fullmarketexperimental' else None
        started=now()
        status={
            'state':'Running','requestId':request_id,'targetMode':target,'activeMode':None,
            'requestedAt':req.get('requestedAt'),'startedAt':started,'completedAt':None,
            'message':'Ubuntu host 正在切換 A18_2 operation mode。'
        }
        atomic(STATUS,status)
        if mode is None:
            status.update(state='Failed',completedAt=now(),message=f'Unsupported target mode: {target}')
            atomic(STATUS,status); continue
        try:
            cp=subprocess.run(['python3',str(SWITCH),mode],cwd=ROOT,text=True,capture_output=True,timeout=180)
            output=(cp.stdout+'\n'+cp.stderr).strip()[-4000:]
            if cp.returncode!=0: raise RuntimeError(output or f'switch exited {cp.returncode}')
            status.update(state='Completed',activeMode=target,completedAt=now(),message='模式切換完成。 '+output[-800:])
        except Exception as exc:
            status.update(state='Failed',completedAt=now(),message=f'模式切換失敗：{exc}')
        atomic(STATUS,status)

if __name__=='__main__': main()
