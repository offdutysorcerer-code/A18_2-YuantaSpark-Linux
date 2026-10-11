import importlib.util,datetime as dt,bisect,json
sp=importlib.util.spec_from_file_location('m','scripts/auto_long_v1_replay.py');m=importlib.util.module_from_spec(sp);sp.loader.exec_module(m)
names={'8039':'台虹','3406':'玉晶光','2303':'聯電','1303':'南亞','1727':'中華化','2409':'友達','3481':'群創','3042':'晶技','6257':'矽格','8150':'南茂','8042':'金山電','2492':'華新科','2327':'國巨*','3665':'貿聯-KY','6456':'GIS-KY','3673':'TPK-KY'}
def run(s,mode,rule):
 bars=m.fetch('http://127.0.0.1:5221',s,'2026-10-08',1);b5=m.fetch('http://127.0.0.1:5221',s,'2026-10-08',5);times=[m.at(x) for x in bars];tr=[];blocks=[];last=None
 for sig in m.signal_times(b5,mode):
  if sig.time()>=dt.time(9,30) or len(tr)>=2 or (last is not None and sig<=last):continue
  i=bisect.bisect_left(times,sig)
  if i>=len(times) or times[i].time()>=dt.time(9,30) or (times[i]-sig).total_seconds()>8:continue
  entry=float(bars[i]['open'])
  if tr and tr[0]['netPct']<0:
   first=tr[0]; start=bisect.bisect_right(times,dt.datetime.fromisoformat(first['exit']))
   lo=min([float(x['low']) for x in bars[start:i]] or [entry]);exit_price=first['exitPrice']
   target={'A':exit_price,'B':exit_price*1.01,'C':max(exit_price,lo*1.01),'BC':max(exit_price*1.01,lo*1.01)}[rule]
   if entry<target:blocks.append({'at':sig.isoformat(),'entry':entry,'threshold':round(target,5),'low':lo});continue
  peak=entry;ex=None
  for k in range(i+1,len(bars)):
   b=bars[k];level=max(entry*.99,peak*.985)
   if float(b['open'])<=level:ex=(k,float(b['open']),'OPEN_BELOW_STOP');break
   if float(b['low'])<=level:ex=(k,level,'PRIOR_WATERMARK_STOP');break
   peak=max(peak,float(b['high']))
  if ex is None:ex=(len(bars)-1,float(bars[-1]['close']),'DATA_END_MARK')
  k,price,reason=ex;last=times[k];net=(price*(1-.000855-.0015)/(entry*(1+.000855))-1)*100
  tr.append({'entry':times[i].isoformat(),'entryPrice':entry,'exit':times[k].isoformat(),'exitPrice':price,'netPct':net,'reason':reason})
 factor=1;cash=0
 for t in tr:factor*=1+t['netPct']/100;cash+=(t['exitPrice']*(1-.000855-.0015)-t['entryPrice']*(1+.000855))*1000
 return {'symbol':s,'name':names[s],'trades':tr,'blocked':blocks,'netPct':round((factor-1)*100,4),'netTwd':round(cash,2)}
r={}
for rule in ['A','B','C','BC']:
 r[rule]={}
 for mode in ['A','B']:
  rows=[run(s,mode,rule) for s in names];r[rule][mode]=rows
  print(rule,mode,'trades',sum(len(x['trades']) for x in rows),'blocks',sum(len(x['blocked']) for x in rows),'profit',round(sum(x['netTwd'] for x in rows),2),flush=True)
  for x in rows:print(x['symbol'],x['netPct'],len(x['trades']),len(x['blocked']),flush=True)
open('/tmp/compare_abc_result.json','w').write(json.dumps(r,ensure_ascii=False,indent=2))
