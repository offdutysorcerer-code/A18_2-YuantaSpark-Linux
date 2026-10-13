#!/usr/bin/env python3
"""Read-only trend-mirror study. Completed 5s candles update a monotone stop for the NEXT second.
No brokerage, runtime21, PAPER, or prod writes.
"""
import importlib.util,datetime as dt,bisect,json
from pathlib import Path
sp=importlib.util.spec_from_file_location('v2','scripts/auto_long_v1_replay.py');v2=importlib.util.module_from_spec(sp);sp.loader.exec_module(v2)
names={'8039':'台虹','3406':'玉晶光','2303':'聯電','1303':'南亞','1727':'中華化','2409':'友達','3481':'群創','3042':'晶技','6257':'矽格','8150':'南茂','8042':'金山電','2492':'華新科','2327':'國巨*','3665':'貿聯-KY','6456':'GIS-KY','3673':'TPK-KY'}

def replay(b1,b5,mode,rule):
 times=[v2.at(x) for x in b1]; signals=v2.signal_times(b5,mode); completed=[v2.at(x)+dt.timedelta(seconds=5) for x in b5];tr=[];blocked=[];last=None
 for sig in signals:
  if sig.time()>=dt.time(9,30) or len(tr)>=2 or (last and sig<=last):continue
  i=bisect.bisect_left(times,sig)
  if i>=len(times) or times[i].time()>=dt.time(9,30) or (times[i]-sig).total_seconds()>8:continue
  entry=float(b1[i]['open'])
  if tr and tr[0]['netPct']<0 and entry<tr[0]['exitPrice']*1.03:
   blocked.append(sig.isoformat());continue
  peak=entry;mirror=entry*.99;why='DATA_END_MARK';exit_idx=len(b1)-1;out=float(b1[-1]['close']);last_done=bisect.bisect_right(completed,times[i])-1
  for k in range(i+1,len(b1)):
   bar=b1[k];threshold=max(entry*.99,peak*.985,mirror)
   # At timestamp k, threshold is fixed from all bars complete strictly before k.
   if float(bar['open'])<=threshold:exit_idx=k;out=float(bar['open']);why='OPEN_BELOW_STOP';break
   if float(bar['low'])<=threshold:exit_idx=k;out=threshold;why='TRAIL_OR_MIRROR_STOP' if mirror>entry*.99 else 'BASE_STOP';break
   peak=max(peak,float(bar['high']))
   # Update for the next 1s candle only. Three completed 5s falling closes,
   # decreasing close momentum and no >0.3% upside achieved since entry.
   curr=bisect.bisect_right(completed,times[k]+dt.timedelta(seconds=1))-1
   for j in range(max(last_done+1,2),curr+1):
    if completed[j]<=times[i]:continue
    c0,c1,c2=[float(b5[z]['close']) for z in (j-2,j-1,j)]
    if c2<c1<c0 and c2<entry and peak<=entry*1.003:
     # Inverted cumulative 10s downward displacement anchored at base stop.
     # 50% mirror response, capped at -0.4% to avoid ultra-tight stops.
     mirror=max(mirror,min(entry*.996,entry*.99+max(0,c0-c2)*.5))
   last_done=max(last_done,curr)
  last=times[exit_idx];net=(out*(1-.000855-.0015)/(entry*(1+.000855))-1)*100
  tr.append({'entry':times[i].isoformat(),'entryPrice':entry,'exit':last.isoformat(),'exitPrice':out,'netPct':net,'reason':why,'mirrorStop':mirror})
 comp=1;cash=0
 for x in tr:comp*=1+x['netPct']/100;cash+=(x['exitPrice']*(1-.000855-.0015)-x['entryPrice']*(1+.000855))*1000
 return {'trades':tr,'blocked':len(blocked),'netPct':round((comp-1)*100,4),'netTwd':round(cash,2)}

def main():
 result={x:{} for x in ['A','B']}
 for sym,name in names.items():
  b1=v2.fetch('http://127.0.0.1:5221',sym,'2026-10-08',1);b5=v2.fetch('http://127.0.0.1:5221',sym,'2026-10-08',5)
  for mode in ['A','B']:
   x=replay(b1,b5,mode,'mirror');x.update(symbol=sym,name=name);result[mode][sym]=x
  print('DONE',sym,name,flush=True)
 for mode,rows in result.items():
  print('MODE',mode,'trades',sum(len(v['trades']) for v in rows.values()),'blocked',sum(v['blocked'] for v in rows.values()),'net',round(sum(v['netTwd'] for v in rows.values()),2))
  for v in rows.values():print(v['symbol'],v['name'],len(v['trades']),v['netPct'],v['netTwd'])
 Path('reports/auto-long/2026-10-08-watchlist16-trend-mirror.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
if __name__=='__main__':main()
