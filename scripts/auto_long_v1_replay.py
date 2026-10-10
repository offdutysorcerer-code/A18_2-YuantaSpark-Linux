#!/usr/bin/env python3
"""Offline, read-only V2-60s replay against A18_2 staging /api/bars.
No calls to AutoLong PAPER ledger, Runtime21, or any order endpoint.
Uses 5s completed close signals; executes on first observed subsequent 1s bar.
The conservative prior-watermark stop convention mirrors the staging simulator.
"""
import argparse,bisect,datetime as dt,json,urllib.request
from pathlib import Path

def fetch(base,symbol,date,interval):
 url=f'{base.rstrip("/")}/api/bars/{symbol}?date={date}&interval={interval}'
 with urllib.request.urlopen(url,timeout=35) as response: payload=json.load(response)
 if payload.get('source')!='A18_10_SECOND' or not payload.get('hasData'):
  raise ValueError(f'{symbol} interval={interval} missing trusted second warehouse: {payload.get("dataState")}')
 return sorted(payload['bars'],key=lambda row:row['startTime'])
def at(row):return dt.datetime.fromisoformat(row['startTime'])
def signal_times(bars,mode):
 prev=None;up=[];output=[]
 for b in bars:
  t=at(b);c=b['close']
  if prev is not None:
   if c>prev:
    up.append(t)
    while up and t+dt.timedelta(seconds=5)-up[0]>dt.timedelta(seconds=60):up.pop(0)
    if len(up)==3:
     output.append(t+dt.timedelta(seconds=5))
     up=up[-2:] if mode=='B' else []
   elif c<prev:up=[]
  prev=c
 return output

def replay(symbol,date,base,mode,max_entries,budget,quantity,fee_rate=.000855,tax_rate=.0015):
 bars5=fetch(base,symbol,date,5);bars1=fetch(base,symbol,date,1)
 if not bars1:return {'symbol':symbol,'error':'NO_1S_BARS'}
 times=[at(b) for b in bars1];signals=signal_times(bars5,mode)
 last_exit=None;spent=0.;trades=[];skips={};cutoff=dt.time(9,30)
 def skip(reason):skips[reason]=skips.get(reason,0)+1
 for signal in signals:
  if signal.time()>=cutoff:skip('AFTER_CUTOFF');continue
  if last_exit is not None and signal<=last_exit:skip('POSITION_OPEN');continue
  if len(trades)>=max_entries:skip('ENTRY_LIMIT');continue
  idx=bisect.bisect_left(times,signal)
  if idx>=len(times) or times[idx].date()!=signal.date() or times[idx].time()>=cutoff:skip('NO_PRE_CUTOFF_FILL');continue
  if (times[idx]-signal).total_seconds()>8:skip('STALE_FILL_GT_8S');continue
  entry=float(bars1[idx]['open'])
  if spent+entry*quantity>budget:skip('BUY_ONLY_BUDGET');continue
  peak=entry;exit_idx=None;exit_price=None;why=None
  for k in range(idx+1,len(bars1)):
   b=bars1[k];p=max(entry*.99,peak*.985)
   if float(b['open'])<=p:exit_idx=k;exit_price=float(b['open']);why='OPEN_BELOW_STOP';break
   if float(b['low'])<=p:exit_idx=k;exit_price=p;why='PRIOR_WATERMARK_STOP';break
   peak=max(peak,float(b['high']))
  if exit_idx is None:exit_idx=len(bars1)-1;exit_price=float(bars1[exit_idx]['close']);why='DATA_END_MARK'
  spent+=entry*quantity;last_exit=times[exit_idx]
  gross=exit_price/entry-1
  net=(exit_price*(1-fee_rate-tax_rate))/(entry*(1+fee_rate))-1
  trades.append({'signal':signal.isoformat(),'entry':times[idx].isoformat(),'entryPrice':entry,'exit':times[exit_idx].isoformat(),'exitPrice':round(exit_price,6),'reason':why,'grossPct':round(gross*100,4),'netPct':round(net*100,4),'buyAmount':round(entry*quantity,2),'maxObservedHigh':peak})
 gross=1.;net=1.
 for t in trades:gross*=1+t['grossPct']/100;net*=1+t['netPct']/100
 return {'symbol':symbol,'date':date,'mode':mode,'bars5':len(bars5),'bars1':len(bars1),'signals':len(signals),'pre930Signals':sum(t.time()<cutoff for t in signals),'trades':trades,'tradesCount':len(trades),'buyAmountTotal':round(spent,2),'grossCompoundedPct':round((gross-1)*100,4),'netCompoundedPct':round((net-1)*100,4),'skips':skips}

def main():
 p=argparse.ArgumentParser();p.add_argument('--url',default='http://127.0.0.1:5221');p.add_argument('--date',default='2026-10-08');p.add_argument('--symbols',nargs='+',default=['3048','1301','1718','3416','6152','5225']);p.add_argument('--mode',choices=['A','B'],default='A');p.add_argument('--max-entries',type=int,default=10);p.add_argument('--budget',type=float,default=10000000);p.add_argument('--quantity',type=int,default=1000);p.add_argument('--output',default='');a=p.parse_args()
 results=[replay(symbol,a.date,a.url,a.mode,a.max_entries,a.budget,a.quantity) for symbol in a.symbols]
 if a.output:Path(a.output).write_text(json.dumps(results,ensure_ascii=False,indent=2)+'\n')
 for x in results:
  print(x['symbol'],'trades',x.get('tradesCount'),'gross',x.get('grossCompoundedPct'),'net',x.get('netCompoundedPct'),'skips',x.get('skips'))
  for trade in x.get('trades',[]):print(' ',trade['signal'][11:19],trade['entry'][11:19],trade['entryPrice'],'=>',trade['exit'][11:19],trade['exitPrice'],trade['reason'])
if __name__=='__main__':main()
