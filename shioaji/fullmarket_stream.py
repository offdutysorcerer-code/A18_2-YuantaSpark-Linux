from __future__ import annotations
import argparse, json, os, queue, signal, threading, time, urllib.request
from datetime import datetime, timezone
from pathlib import Path
import shioaji as sj

def ts_ms(value):
    if isinstance(value, datetime):
        if value.tzinfo is None:
            value=value.replace(tzinfo=timezone.utc)
        return int(value.timestamp()*1000)
    return int(time.time()*1000)

def load_symbols(plan, session):
    d=json.loads(Path(plan).read_text(encoding="utf-8"))
    rows=[r for r in d["symbols"] if r.get("provider")=="shioaji" and str(r.get("providerSession"))==str(session)]
    syms=[str(r["symbol"]).strip() for r in rows]
    if len(syms)!=200: raise RuntimeError(f"session {session} expected 200 symbols, got {len(syms)}")
    return syms

def contract(api,symbol):
    c=api.contracts.get(symbol)
    if c is not None: return c
    raise RuntimeError(f"contract not found: {symbol}")

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--session",type=int,required=True)
    ap.add_argument("--plan",required=True)
    ap.add_argument("--endpoint",required=True)
    args=ap.parse_args()
    key=os.environ.get("SJ_API_KEY","").strip()
    sec=os.environ.get("SJ_SEC_KEY","").strip()
    if not key or not sec: raise RuntimeError("SJ_API_KEY/SJ_SEC_KEY missing")
    symbols=load_symbols(args.plan,args.session)
    stop=threading.Event()
    q=queue.Queue(maxsize=100000)

    def sig(*_): stop.set()
    signal.signal(signal.SIGTERM,sig); signal.signal(signal.SIGINT,sig)

    def sender():
        batch=[]
        deadline=time.monotonic()+0.1
        while not stop.is_set() or not q.empty() or batch:
            try: batch.append(q.get(timeout=max(0.001,deadline-time.monotonic())))
            except queue.Empty: pass
            now=time.monotonic()
            if batch and (len(batch)>=200 or now>=deadline or (stop.is_set() and q.empty())):
                data=json.dumps(batch,separators=(",",":")).encode()
                req=urllib.request.Request(args.endpoint,data=data,headers={"Content-Type":"application/json"},method="POST")
                try:
                    with urllib.request.urlopen(req,timeout=2): pass
                except Exception:
                    pass
                batch=[]; deadline=now+0.1

    def on_tick(exchange,tick):
        code=str(getattr(tick,"code","") or "").strip()
        price=float(getattr(tick,"close",0) or 0)
        if not code or price<=0:return
        row={"provider":"shioaji","session":str(args.session),"symbol":code,"price":price,
             "quantity":int(getattr(tick,"volume",0) or 0),"timestampUnixMs":ts_ms(getattr(tick,"datetime",None))}
        try:q.put_nowait(row)
        except queue.Full:pass

    threading.Thread(target=sender,daemon=True).start()
    api=sj.Shioaji(simulation=os.environ.get("SJ_PRODUCTION","true").lower() not in {"1","true","yes","on"})
    accepted=[]
    try:
        time.sleep(max(0,(args.session-1)*3))
        login_ok=False
        for attempt in range(1,13):
            try:
                api.login(api_key=key,secret_key=sec,subscribe_trade=False)
                login_ok=True
                break
            except Exception as exc:
                message=str(exc)
                if "451" not in message and "Too Many Connections" not in message:
                    raise
                delay=min(30,10+attempt*2)
                print(json.dumps({"event":"login-retry","session":args.session,"attempt":attempt,"delaySeconds":delay,"reason":"451 Too Many Connections"}),flush=True)
                time.sleep(delay)
        if not login_ok:
            raise RuntimeError(f"session {args.session} login quota did not recover after retries")
        api.set_on_tick_stk_v1_callback(on_tick)
        for i,symbol in enumerate(symbols,1):
            c=contract(api,symbol)
            api.subscribe(c,quote_type=sj.QuoteType.Tick)
            accepted.append(c)
            if i%50==0: time.sleep(1)
        print(json.dumps({"event":"subscribed","session":args.session,"accepted":len(accepted)}),flush=True)
        while not stop.is_set(): time.sleep(0.2)
    finally:
        stop.set()
        for c in accepted:
            try: api.unsubscribe(c,quote_type=sj.QuoteType.Tick)
            except Exception: pass
        try: api.logout()
        except Exception: pass

if __name__=="__main__":
    main()
