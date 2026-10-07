#!/usr/bin/env python3
from __future__ import annotations
import argparse, pathlib, subprocess, sys, time, urllib.request, json

ROOT=pathlib.Path(__file__).resolve().parents[1]
ENV=ROOT/'.env'
A18_22=pathlib.Path('/mnt/d/AIProjects/A18_22-全市場即時觀察台')


def set_env(key,value):
    lines=ENV.read_text().splitlines() if ENV.exists() else []
    out=[]; found=False
    for line in lines:
        if line.startswith(key+'='):
            out.append(f'{key}={value}'); found=True
        else: out.append(line)
    if not found: out.append(f'{key}={value}')
    ENV.write_text('\n'.join(out)+'\n')


def run(*args, cwd=ROOT):
    subprocess.run(args,cwd=cwd,check=True)


def api():
    for _ in range(30):
        try:
            with urllib.request.urlopen('http://127.0.0.1:5220/api/operation-mode',timeout=2) as r:
                return json.load(r)
        except Exception: time.sleep(1)
    raise RuntimeError('A18_2 operation-mode API did not become ready')


def main():
    ap=argparse.ArgumentParser(description='Switch A18_2 between Legacy and FullMarketExperimental.')
    ap.add_argument('mode',choices=['legacy','fullmarket'])
    ap.add_argument('--build',action='store_true',help='build image before recreating container')
    args=ap.parse_args()
    if args.mode=='fullmarket':
        manager=A18_22/'scripts'/'manage_shioaji_collectors.py'
        if manager.exists(): run('python3',str(manager),'stop',cwd=A18_22)
        set_env('A18_OPERATION_MODE','FullMarketExperimental')
        set_env('A18_FULLMARKET_START_SHIOAJI','true')
        expected='FullMarketExperimental'
    else:
        set_env('A18_OPERATION_MODE','Legacy')
        set_env('A18_FULLMARKET_START_SHIOAJI','false')
        expected='Legacy'
    cmd=['docker','compose','up','-d','--no-deps','--force-recreate']
    if args.build: cmd.append('--build')
    cmd.append('a18-realtime')
    run(*cmd)
    state=api()
    print(json.dumps(state,ensure_ascii=False))
    if state.get('mode')!=expected:
        raise RuntimeError(f'expected mode {expected}, got {state}')
    if args.mode=='legacy':
        manager=A18_22/'scripts'/'manage_shioaji_collectors.py'
        if manager.exists():
            run('python3',str(manager),'start',cwd=A18_22)

if __name__=='__main__':
    main()
