from __future__ import annotations
import csv, json, os, sys, tempfile
from collections import OrderedDict
from datetime import datetime, time as clock_time, timedelta, timezone
from decimal import Decimal
from pathlib import Path
import shioaji as sj

TW = timezone(timedelta(hours=8))
HEADER = ["timestamp","open","high","low","close","volume","turnover","tick_count"]

def dt_tick(v): return datetime.fromtimestamp(int(v)/1_000_000_000, tz=timezone.utc).replace(tzinfo=TW)
def dec(v): return Decimal(str(v))
def txt(v):
    s=format(v,"f")
    return s.rstrip("0").rstrip(".") if "." in s else s

def aggregate(ticks, trade_date):
    expected=datetime.strptime(trade_date,"%Y-%m-%d").date(); grouped=OrderedDict()
    for ns,px0,vol0 in zip(ticks.ts,ticks.close,ticks.volume):
        ts=dt_tick(ns).replace(microsecond=0)
        if ts.date()!=expected or not clock_time(9,0)<=ts.time()<=clock_time(13,30): continue
        px=dec(px0); vol=int(vol0)
        if px<=0 or vol<0: continue
        b=grouped.get(ts)
        if b is None: grouped[ts]=[px,px,px,px,vol,px*vol,1]
        else:
            b[1]=max(b[1],px); b[2]=min(b[2],px); b[3]=px; b[4]+=vol; b[5]+=px*vol; b[6]+=1
    return [[ts.strftime("%Y-%m-%d %H:%M:%S"),txt(b[0]),txt(b[1]),txt(b[2]),txt(b[3]),b[4],txt(b[5]),b[6]] for ts,b in grouped.items()]

def contract(api,symbol):
    # Shioaji 1.7+ unified resolver supports stocks, indexes and futures.
    c = api.contracts.get(symbol)
    if c is not None:
        return c
    # Compatibility fallback for older stock collections.
    stocks = api.Contracts.Stocks
    for market_name in ("TSE", "OTC", "OES"):
        market = getattr(stocks, market_name, None)
        if market is None:
            continue
        try:
            c = market[symbol]
        except (KeyError, IndexError, TypeError):
            c = None
        if c is not None:
            return c
    raise RuntimeError(f"contract not found: {symbol}")

def main():
    symbol,date,target=sys.argv[1],sys.argv[2],Path(sys.argv[3])
    key=os.environ.get("SJ_API_KEY","").strip(); secret=os.environ.get("SJ_SEC_KEY","").strip()
    if not key or not secret: raise RuntimeError("SJ_API_KEY/SJ_SEC_KEY missing")
    api=sj.Shioaji(simulation=os.environ.get("SJ_PRODUCTION","true").lower() not in {"1","true","yes","on"})
    try:
        api.login(api_key=key, secret_key=secret, subscribe_trade=False)
        ticks=api.ticks(contract=contract(api,symbol), date=date)
        rows=aggregate(ticks,date)
        if not rows:
            print(json.dumps({"status":"NO_DATA","symbol":symbol,"date":date,"rawTicks":len(ticks.ts)})); return 2
        target.parent.mkdir(parents=True,exist_ok=True)
        fd,tmp=tempfile.mkstemp(prefix=".a18-",suffix=".tmp",dir=target.parent,text=True); os.close(fd)
        try:
            with open(tmp,"w",newline="",encoding="utf-8") as f:
                w=csv.writer(f,lineterminator="\n"); w.writerow(HEADER); w.writerows(rows)
            if not target.exists(): os.replace(tmp,target)
            else: os.unlink(tmp)
        finally:
            if os.path.exists(tmp): os.unlink(tmp)
        print(json.dumps({"status":"FETCHED","symbol":symbol,"date":date,"secondBars":len(rows),"rawTicks":len(ticks.ts),"path":str(target)}))
    finally:
        try: api.logout()
        except Exception: pass
if __name__=="__main__": main()
