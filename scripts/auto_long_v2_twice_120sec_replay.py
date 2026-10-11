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

def replay(b1,b5,mode,coefficient,recovery,gate):
 times=[v2.at(x) for x in b1];signals=twice_in_120s(v2.signal_times(b5,mode));done=[v2.at(x)+dt.timedelta(seconds=5) for x in b5];tr=[];blocks=0;last=None
 for sig in signals:
  if sig.time()>=dt.time(9,30) or len(tr)>=2 or (last and sig<=last):continue
  i=bisect.bisect_left(times,sig)
  if i>=len(times) or times[i].time()>=dt.time(9,30) or (times[i]-sig).total_seconds()>8:continue
  entry=float(b1[i]['open'])
  if tr and tr[0]['netPct']<0 and entry<tr[0]['exitPrice']*recovery:blocks+=1;continue
  # A causal micro-ZigZag HIGH confirmation occurs after a >=0.5%
  # close reversal from the running extreme, not at the retrospective high.
  zz_dir=0;zz_hi=float(b1[i]['close']);zz_lo=zz_hi;zz_ext=zz_hi;zz_confirm_down=False
  peak=entry;mirror=entry*.99;why='DATA_END_MARK';exidx=len(b1)-1;out=float(b1[-1]['close']);last_done=bisect.bisect_right(done,times[i])-1;mirror_updates=0;mirror_exit=False
  for k in range(i+1,len(b1)):
   b=b1[k];base=max(entry*.99,peak*.985);threshold=max(base,mirror)
   if float(b['open'])<=threshold:exidx=k;out=float(b['open']);why='OPEN_BELOW_STOP';mirror_exit=mirror>base;break
   if float(b['low'])<=threshold:exidx=k;out=threshold;why='MIRROR_STOP' if mirror>base else 'BASE_OR_TRAIL_STOP';mirror_exit=mirror>base;break
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
   curr=bisect.bisect_right(done,times[k]+dt.timedelta(seconds=1))-1
   for j in range(max(last_done+1,2),curr+1):
    if done[j]<=times[i]:continue
    c0,c1,c2=[float(b5[z]['close']) for z in (j-2,j-1,j)]
    if c2<c1<c0 and c2<entry and peak<=entry*1.003 and (not gate or (zz_confirm_down and zz_dir==-1)):
     newer=max(mirror,min(entry*.996,entry*.99+max(0,c0-c2)*coefficient))
     if newer>mirror:mirror_updates+=1
     mirror=newer
   last_done=max(last_done,curr)
  last=times[exidx];net=(out*(1-.000855-.0015)/(entry*(1+.000855))-1)*100
  tr.append({'entry':times[i].isoformat(),'entryPrice':entry,'exit':last.isoformat(),'exitPrice':out,'netPct':net,'reason':why,'mirrorStop':mirror,'mirrorUpdates':mirror_updates,'mirrorWasBindingAtExit':mirror_exit})
 comp=1.;cash=0.
 for x in tr:comp*=1+x['netPct']/100;cash+=(x['exitPrice']*(1-.000855-.0015)-x['entryPrice']*(1+.000855))*1000
 return {'trades':tr,'blocked':blocks,'netPct':round((comp-1)*100,4),'netTwd':round(cash,2)}
def main():
 configs=[(recovery,gate) for recovery in (1.02,1.03) for gate in (False,True)];result={f'{recovery:.2f}_{int(gate)}':{'A':{},'B':{}} for recovery,gate in configs}
 for s,name in names.items():
  b1=v2.fetch('http://127.0.0.1:5221',s,'2026-10-08',1);b5=v2.fetch('http://127.0.0.1:5221',s,'2026-10-08',5)
  for recovery,gate in configs:
   key=f'{recovery:.2f}_{int(gate)}'
   for mode in 'AB':
    row=replay(b1,b5,mode,.5,recovery,gate);row.update(symbol=s,name=name);result[key][mode][s]=row
  print('DONE',s,flush=True)
 for recovery,gate in configs:
  key=f'{recovery:.2f}_{int(gate)}'
  for mode in 'AB':
   rows=list(result[key][mode].values())
   print('RESULT',key,mode,'net',round(sum(x['netTwd'] for x in rows),2),'trades',sum(len(x['trades']) for x in rows),'blocked',sum(x['blocked'] for x in rows),'mirrorExits',sum(sum(t['mirrorWasBindingAtExit'] for t in x['trades']) for x in rows),'mirrorUpdates',sum(sum(t['mirrorUpdates'] for t in x['trades']) for x in rows))
   print('FOCUS',[(x['symbol'],x['netPct'],[(t['entry'][11:19],t['exit'][11:19],t['reason']) for t in x['trades']]) for x in rows if x['symbol'] in ('8039','6257')])
 path=Path('reports/auto-long/2026-10-08-watchlist16-v2-twice-120sec-102-103.json');path.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
if __name__=='__main__':main()
