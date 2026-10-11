#!/usr/bin/env python3
"""Read-only mirror response coefficient sweep, using trusted staging second warehouse."""
import importlib.util,datetime as dt,bisect,json
from pathlib import Path
sp=importlib.util.spec_from_file_location('v2','scripts/auto_long_v1_replay.py');v2=importlib.util.module_from_spec(sp);sp.loader.exec_module(v2)
names={'8039':'台虹','3406':'玉晶光','2303':'聯電','1303':'南亞','1727':'中華化','2409':'友達','3481':'群創','3042':'晶技','6257':'矽格','8150':'南茂','8042':'金山電','2492':'華新科','2327':'國巨*','3665':'貿聯-KY','6456':'GIS-KY','3673':'TPK-KY'}
def twice_in_120s(signals):
 # Pair non-overlapping, consecutive completed V2 signals; reset after second confirmation.
 out=[];pending=None
 for signal in signals:
  if pending is None:pending=signal
  elif (signal-pending).total_seconds()<=120:
   out.append(signal);pending=None
  else:pending=signal
 return out

def regime_signals(b1,b5,mode,reference):
 times=[v2.at(b) for b in b1]; pending=None;ready=[]
 for sig in v2.signal_times(b5,mode):
  i=bisect.bisect_left(times,sig)
  if i>=len(times) or (times[i]-sig).total_seconds()>8: continue
  price=float(b1[i]['open'])
  if price>reference:
   ready.append(sig);pending=None
  elif pending is None or (sig-pending).total_seconds()>120:
   pending=sig
  else:
   ready.append(sig);pending=None
 return ready

def causal_up_trends(b5):
 """Exploratory causal uptrend events, not Swing T9 parity. Signals at completed 5s bar.
 Require a 30s directional displacement >=0.45%, efficiency >=0.58,
 and a price close above previous 30s highs, with >=3 rising steps.
 """
 events=[]
 for i in range(6,len(b5)):
  sample=b5[i-6:i+1];closes=[float(x['close']) for x in sample]
  start=closes[0];finish=closes[-1];travel=sum(abs(closes[k]-closes[k-1]) for k in range(1,len(closes)))
  change=(finish/start-1)*100 if start else 0
  eff=(finish-start)/travel if travel else 0
  rising=sum(closes[k]>closes[k-1] for k in range(1,len(closes)))
  if change>=.45 and eff>=.58 and rising>=3 and finish>=max(float(x['high']) for x in sample[:-1]):
   events.append(v2.at(b5[i])+dt.timedelta(seconds=5))
 return events

def replay(b1,b5,mode,coefficient,recovery,gate,reference,opening,exit_variant,trend_window=None):
 uptrends=causal_up_trends(b5);trend_rejects=0;times=[v2.at(x) for x in b1];signals=sorted(set(v2.signal_times(b5,'A'))|set(v2.signal_times(b5,'B')));a_signals=set(v2.signal_times(b5,'A'));b_signals=set(v2.signal_times(b5,'B'));pending=None;price_rejects=0;done=[v2.at(x)+dt.timedelta(seconds=5) for x in b5];tr=[];blocks=0;last=None
 for sig in signals:
  if len(tr)>=2:continue
  if last and sig<=last:pending=None;continue
  i=bisect.bisect_left(times,sig)
  if i>=len(times) or (times[i]-sig).total_seconds()>8:continue
  entry=float(b1[i]['open'])
  if entry>max(reference,opening):
   if sig not in b_signals:continue
   pending=None
  elif sig not in a_signals:continue
  elif pending is None or (sig-pending[0]).total_seconds()>120:
   pending=(sig,entry);continue
  else:
   if entry<=pending[1]:
    price_rejects+=1
    # Reject the pair entirely; require two fresh V2 signals.
    pending=None;continue
   pending=None
  if trend_window is not None:
   prior_trend=bisect.bisect_right(uptrends,sig)-1
   if prior_trend<0 or (sig-uptrends[prior_trend]).total_seconds()>trend_window:
    trend_rejects+=1;continue
  # Experiment: no post-loss price-recovery gate; C signal rules remain unchanged.
  # A causal micro-ZigZag HIGH confirmation occurs after a >=0.5%
  # close reversal from the running extreme, not at the retrospective high.
  zz_dir=0;zz_hi=float(b1[i]['close']);zz_lo=zz_hi;zz_ext=zz_hi;zz_confirm_down=False
  peak=entry;mirror=entry*.99;why='DATA_END_MARK';exidx=len(b1)-1;out=float(b1[-1]['close']);last_done=bisect.bisect_right(done,times[i])-1;mirror_updates=0;mirror_exit=False;exit_triggered=False;close_history=[];low_history=[];completed_closes=[]
  for k in range(i+1,len(b1)):
   b=b1[k];base=max(entry*.99,peak*(.993 if len(tr)>=1 else .985));threshold=max(base,mirror)
   if float(b['open'])<=threshold:exidx=k;out=float(b['open']);why='OPEN_BELOW_STOP';mirror_exit=mirror>base;break
   if float(b['low'])<=threshold:exidx=k;out=threshold;why='MIRROR_STOP' if mirror>base else 'BASE_OR_TRAIL_STOP';mirror_exit=mirror>base;break
   # Completed second k becomes evidence only for trading at k+1.
   close_history.append(float(b['close']));low_history.append(float(b['low']))
   # B: confirmed 5-second momentum turns downward by >=0.20% vs 15-second prior close,
   # with latest two completed 5s closes falling; C: current completed 1s close
   # breaks the minimum low of the preceding 30 completed seconds by >=0.10%.
   curr=bisect.bisect_right(done,times[k]+dt.timedelta(seconds=1))-1
   for j in range(max(last_done+1,0),curr+1):
    if done[j]<=times[i]:continue
    completed_closes.append(float(b5[j]['close']))
   last_done=max(last_done,curr)
   momentum=(len(completed_closes)>=4 and completed_closes[-1]<completed_closes[-2] and completed_closes[-2]<completed_closes[-3] and completed_closes[-1]<=completed_closes[-4]*.998)
   structure=(len(low_history)>=31 and close_history[-1]<min(low_history[-31:-1])*.999)
   invalid=(exit_variant in ('MOMENTUM','MIRROR_MOMENTUM') and momentum or exit_variant=='STRUCTURE' and structure or exit_variant=='BOTH' and (momentum or structure))
   if invalid:
    # Decision confirmed at end of second k: fill at NEXT second open (not current close).
    if k+1<len(b1):
     exidx=k+1;out=float(b1[k+1]['open']);why='MOMENTUM_FADE' if momentum else 'STRUCTURE_BREAK';exit_triggered=True;break
   peak=max(peak,float(b['high']))
   # Incorporate just-finished second only for subsequent seconds.
   zz_p=float(b['close'])
   if zz_dir==0:
    zz_hi=max(zz_hi,zz_p);zz_lo=min(zz_lo,zz_p)
    if (zz_hi-zz_p)/zz_hi>=.005:zz_dir=-1;zz_ext=zz_p;zz_confirm_down=True
    elif (zz_p-zz_lo)/zz_lo>=.005:zz_dir=1;zz_ext=zz_p
   elif zz_dir==1:
    if zz_p>=zz_ext:zz_ext=zz_p
    elif (zz_ext-zz_p)/zz_ext>=.005:zz_dir=-1;zz_ext=zz_p;zz_confirm_down=True
   else:
    if zz_p<=zz_ext:zz_ext=zz_p
    elif (zz_p-zz_ext)/zz_ext>=.005:zz_dir=1;zz_ext=zz_p
   # Mirror is live immediately; use only the completed 1s close.
   # No three 5-second declining closes, no below-entry trigger gate,
   # and no +0.3% peak eligibility gate.
   drop=max(0.0,entry-float(b['close'])) if exit_variant in ('MIRROR','MIRROR_MOMENTUM') else 0.0
   newer=max(mirror,min(entry*.996,entry*.99+drop*coefficient))
   if newer>mirror:mirror_updates+=1
   mirror=newer
  last=times[exidx];net=(out*(1-.000855-.0015)/(entry*(1+.000855))-1)*100
  tr.append({'entry':times[i].isoformat(),'entryPrice':entry,'exit':last.isoformat(),'exitPrice':out,'netPct':net,'reason':why,'mirrorStop':mirror,'mirrorUpdates':mirror_updates,'mirrorWasBindingAtExit':mirror_exit,'directionExit':exit_triggered})
 comp=1.;cash=0.
 for x in tr:comp*=1+x['netPct']/100;cash+=(x['exitPrice']*(1-.000855-.0015)-x['entryPrice']*(1+.000855))*1000
 return {'trades':tr,'blocked':blocks,'priceRejected':price_rejects,'trendRejected':trend_rejects,'trendEventCount':len(uptrends),'netPct':round((comp-1)*100,4),'netTwd':round(cash,2)}
def main():
 configs=[(1.0,'MIRROR_MOMENTUM')];result={k:{} for k in ('BASE','TREND_15','TREND_30','TREND_60','TREND_120')}
 for s,name in names.items():
  b1=v2.fetch('http://127.0.0.1:5221',s,'2026-10-08',1);b5=v2.fetch('http://127.0.0.1:5221',s,'2026-10-08',5)
  prior=v2.fetch('http://127.0.0.1:5221',s,'2026-10-07',5)
  assert prior and v2.at(prior[-1]).date()==dt.date(2026,10,7)
  reference=float(prior[-1]['close'])
  opening=float(b1[0]['open'])
  for recovery,variant in configs:
   key=f'{recovery:.2f}_{variant}'
   for mode in ('BASE','TREND_15','TREND_30','TREND_60','TREND_120'):
    row=replay(b1,b5,'C',.5,recovery,False,reference,opening,variant,None if mode=='BASE' else int(mode.split('_')[1]));row.update(symbol=s,name=name,priorClose=reference,dayOpen=opening);result[mode][s]=row
  print('DONE',s,flush=True)
 for mode in ('BASE','TREND_15','TREND_30','TREND_60','TREND_120'):
  rows=list(result[mode].values());print('RESULT',mode,'net',round(sum(x['netTwd'] for x in rows),2),'trades',sum(len(x['trades']) for x in rows),'trendRejects',sum(x['trendRejected'] for x in rows),flush=True)
 Path('reports/auto-long/2026-10-08-watchlist16-C-trend-TTL-sweep.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
if __name__=='__main__':main()
