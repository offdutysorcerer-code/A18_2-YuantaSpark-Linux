"""Read-only 2026-10-08 16-symbol replay. Stop opening/holding at 09:30;
second candidate is rejected only if first trade had negative NET P&L and
second entry candidate price < 0.8 * first entry price.
"""
import importlib.util, bisect, datetime as dt, json, argparse
from pathlib import Path
P=Path(__file__).with_name('auto_long_v1_replay.py')
spec=importlib.util.spec_from_file_location('base_replay',P);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
NAMES={'8039':'台虹','3406':'玉晶光','2303':'聯電','1303':'南亞','1727':'中華化','2409':'友達','3481':'群創','3042':'晶技','6257':'矽格','8150':'南茂','8042':'金山電','2492':'華新科','2327':'國巨*','3665':'貿聯-KY','6456':'GIS-KY','3673':'TPK-KY'}
def replay(symbol,mode='A',rule80=True,flat930=True,base='http://127.0.0.1:5221'):
 b1=m.fetch(base,symbol,'2026-10-08',1);b5=m.fetch(base,symbol,'2026-10-08',5)
 stamps=[m.at(b) for b in b1];last=None;rows=[];skips={};first=None;spent=0
 def skip(k):skips[k]=skips.get(k,0)+1
 cut=dt.time(9,30)
 for sig in m.signal_times(b5,mode):
  if sig.time()>=cut:skip('AFTER_0930');continue
  if len(rows)>=2:skip('MAX_2');continue
  if last is not None and sig<=last:skip('POSITION_OPEN');continue
  i=bisect.bisect_left(stamps,sig)
  if i>=len(stamps) or stamps[i].time()>=cut:skip('NO_FILL_BEFORE_0930');continue
  if (stamps[i]-sig).total_seconds()>8:skip('STALE_FILL');continue
  price=float(b1[i]['open'])
  if first is not None and rule80 and first['netPct']<0 and price<first['entryPrice']*.8:
   skip('FIRST_LOSS_SECOND_BELOW_80_PERCENT');continue
  if spent+1000*price>10000000:skip('BUDGET');continue
  peak=price;ex=None;reason=None;kexit=None
  for k in range(i+1,len(b1)):
   b=b1[k];t=stamps[k]
   if flat930 and t.time()>=cut:break
   p=max(price*.99,peak*.985)
   if float(b['open'])<=p:ex=float(b['open']);reason='OPEN_BELOW_STOP';kexit=k;break
   if float(b['low'])<=p:ex=p;reason='PRIOR_WATERMARK_STOP';kexit=k;break
   peak=max(peak,float(b['high']))
  if ex is None:
   if flat930:
    j=bisect.bisect_left(stamps,dt.datetime.combine(stamps[i].date(),cut,tzinfo=stamps[i].tzinfo))-1
    if j<i:j=i
    kexit=j;ex=float(b1[j]['close']);reason='FLAT_BEFORE_0930'
   else:kexit=len(b1)-1;ex=float(b1[-1]['close']);reason='DATA_END_MARK'
  gross=(ex/price-1)*100;net=(ex*(1-.000855-.0015)/(price*(1+.000855))-1)*100
  row={'signal':sig.isoformat(),'entry':stamps[i].isoformat(),'entryPrice':price,'exit':stamps[kexit].isoformat(),'exitPrice':round(ex,6),'reason':reason,'grossPct':round(gross,4),'netPct':round(net,4)}
  rows.append(row);first=first or row;last=stamps[kexit];spent+=1000*price
 gross=net=1.
 for x in rows:gross*=1+x['grossPct']/100;net*=1+x['netPct']/100
 cash=sum((x['exitPrice']*(1-.000855-.0015)-x['entryPrice']*(1+.000855))*1000 for x in rows)
 return {'symbol':symbol,'name':NAMES[symbol],'mode':mode,'trades':rows,'count':len(rows),'grossPct':round((gross-1)*100,4),'netPct':round((net-1)*100,4),'cashTwd':round(cash,2),'skips':skips}
if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--output',default='');a=ap.parse_args()
 result={'assumptions':{'date':'2026-10-08','maxEntries':2,'secondPriceVsFirst':0.8,'firstLoss':'net negative','cutoff':'flat before 09:30:00','capitalPerTrade':'1000 shares'},'A':[replay(s) for s in NAMES],'B':[replay(s,'B') for s in NAMES],'A_no80':[replay(s,rule80=False) for s in NAMES]}
 if a.output:Path(a.output).write_text(json.dumps(result,ensure_ascii=False,indent=2))
 for key in ['A','B']:
  print(key,'trades',sum(x['count'] for x in result[key]),'cash',round(sum(x['cashTwd'] for x in result[key]),2),'80 rejection',sum(x['skips'].get('FIRST_LOSS_SECOND_BELOW_80_PERCENT',0) for x in result[key]))
  for r in result[key]:print(r['symbol'],r['name'],r['count'],r['grossPct'],r['netPct'],r['cashTwd'],[t['reason'] for t in r['trades']])
