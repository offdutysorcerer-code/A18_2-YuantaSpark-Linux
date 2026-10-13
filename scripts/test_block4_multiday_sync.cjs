const assert=require('node:assert/strict');
const fs=require('node:fs');
const s=fs.readFileSync(process.argv[2],'utf8');
const start=s.indexOf('function syncBlock4MultiDay(){');
const end=s.indexOf("$('trendEnabled').addEventListener",start);
const headStart=s.indexOf("$('multiDay').addEventListener('change'");
const headEnd=s.indexOf('function syncSectionNumbersToggle()',headStart);
assert(start>0&&end>start,'multi-day control functions found');
class Node{
 constructor(value=''){this.value=String(value);this.checked=false;this.disabled=false;this.style={};this.textContent='';this.listeners={};}
 addEventListener(name,fn){(this.listeners[name]??=[]).push(fn);}
 dispatchEvent(event){for(const fn of this.listeners[event.type]||[])fn(event);return true;}
}
const nodes=new Map(), $=id=>nodes.get(id)??nodes.set(id,new Node()).get(id);
for(const id of ['symbol','date','interval','auto','multiDay','lookbackDays','block4Symbol','block4Date','block4Interval','block4Auto','block4MultiDay','block4LookbackDays','block4LookbackWrap','block4Weekday','weekday','block4CompanyName','sSymbol','block4PrevDay','block4NextDay','block4Load'])$(id);
$('symbol').value='1301';$('date').value='2026-10-12';$('interval').value='5';$('weekday').textContent='週一';
$('multiDay').checked=false;$('lookbackDays').value='4';
let loads=0,saves=0;
const api=new Function('$','symbolNames','savePrefs','load','shiftDay','updateWeekday','syncSimpleSymbolFromCanonical','clearInterval','setInterval','state','Event',
 s.slice(start,end)+s.slice(headStart,headEnd)+'\nreturn {syncBlock4TargetControls,syncBlock4MultiDay};')(
 $,{'1301':'台塑'},()=>saves++,()=>loads++,async()=>{},()=>{},()=>{},()=>{},()=>1,{timer:0},Event);
api.syncBlock4TargetControls();
assert.equal($('block4MultiDay').checked,false);
assert.equal($('block4LookbackDays').value,'4');
assert.equal($('block4LookbackDays').disabled,true);
$('block4MultiDay').checked=true;$('block4MultiDay').dispatchEvent(new Event('change'));
assert.equal($('multiDay').checked,true);
assert.equal($('block4LookbackDays').disabled,false);
assert.equal(loads,1,'enabling loads only once');
$('block4LookbackDays').value='3';$('block4LookbackDays').dispatchEvent(new Event('change'));
assert.equal($('lookbackDays').value,'3');assert.equal(loads,2,'changing days reloads');
$('block4LookbackDays').value='500';$('block4LookbackDays').dispatchEvent(new Event('change'));
assert.equal($('lookbackDays').value,'20','maximum clamped');assert.equal($('block4LookbackDays').value,'20');
$('block4LookbackDays').value='-3';$('block4LookbackDays').dispatchEvent(new Event('change'));
assert.equal($('lookbackDays').value,'1','minimum clamped');
$('multiDay').checked=false;$('multiDay').dispatchEvent(new Event('change'));
assert.equal($('block4MultiDay').checked,false,'header toggles mirrored');assert.equal($('block4LookbackDays').disabled,true);
const before=loads;
$('lookbackDays').value='9';$('lookbackDays').dispatchEvent(new Event('change'));
assert.equal($('block4LookbackDays').value,'9');assert.equal(loads,before,'disabled multi-day does not reload');
api.syncBlock4TargetControls();assert.equal($('block4Symbol').value,'1301');assert.equal($('block4Date').value,'2026-10-12');
console.log('PASS: header↔chart synchronized, 1–20 clamp, disabled style, single reload, savePrefs count='+saves);
