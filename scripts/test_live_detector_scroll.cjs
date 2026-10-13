const assert = require('node:assert/strict');
const fs = require('node:fs');
const source = fs.readFileSync(process.argv[2], 'utf8');
const a = source.indexOf('function mutateDetectorWithoutViewportJump(');
const b = source.indexOf('async function preOrderDetectorConfirm(', a);
assert(a > 0 && b > a, 'live detector module found');

class FakePanel {
  constructor() {
    this.style={minHeight:''};this.className='detector-result';
    this.htmlWrites=0;this.textWrites=0;this._html='';this._text='';
    this.clock={textContent:''};this.raw=null;this._attributes=new Set();
  }
  set innerHTML(v) { this._html=v;this._text='';this.htmlWrites++;this.raw=v.includes('class="detector-raw"')?{open:false}:null; }
  get innerHTML() {return this._html}
  set textContent(v) {this._text=v;this._html='';this.textWrites++;this.raw=null}
  get textContent(){return this._text}
  querySelector(sel) {
    if(sel==='.detector-human')return this._html.includes('class="detector-human"')?{}:null;
    if(sel==='.detector-raw')return this.raw;
    if(sel==='[data-detector-clock]')return this._html.includes('data-detector-clock')?this.clock:null;
    return null;
  }
  closest(){return this}
  getBoundingClientRect(){return {height:this._html?250:24,bottom:100}}
  setAttribute(key){this._attributes.add(key)}
  removeAttribute(key){this._attributes.delete(key)}
}

async function test(){
  const el = new FakePanel();
  const controls = {
    probeDetectorResult:el,
    liveDetectorEnabled:{checked:true},
    probeDetectorEnabled:{checked:false},
    probeDetectorVersion:{value:'D2'},
    simDirection:{value:'LONG'},
    symbol:{value:'1301'},
    date:{value:'2026-10-12'}
  };
  const $=id=>controls[id];
  const state={symbol:'1301'};
  let current={version:'D2',advice:'ALLOW',timing:'CONFIRM',timingAdvice:'ALLOW',
    return15s:0.1,return30s:0.2,position30s:80,diagnostic:'ok',timingDiagnostic:'ok'};
  let deferred=null;
  let detectorCalls=0;
  function runEntryDetector(){
    detectorCalls++;
    if(deferred)return new Promise(resolve => deferred(resolve));
    return Promise.resolve({...current});
  }
  const detectorExplanation = () => ({title:'Trend',action:'Go',risk:'Risk',why:['A','B','C']});
  const esc=s=>String(s);
  const browserViewport={scrollY:0,scrollBy({top}){this.scrollY+=top}};
  const api = new Function('$','state','symbolNames','runEntryDetector','detectorExplanation','esc',
    'window','document','requestAnimationFrame',
    source.slice(a,b)+'\nreturn {renderLiveDetector,paintStableDetectorPanel,syncLiveDetector,mutateDetectorWithoutViewportJump};')(
      $,state,{'1301':'台塑'},runEntryDetector,detectorExplanation,esc,
      browserViewport,{visibilityState:'visible'},fn=>fn());

  await api.renderLiveDetector();
  assert.equal(el.htmlWrites,1,'first calculation paints result');
  assert(el.innerHTML.includes('data-detector-clock'));
  const firstContent=el.innerHTML;
  const firstHtmlWrites=el.htmlWrites, firstTextWrites=el.textWrites;
  let finish;
  deferred=resolve=>{finish=resolve};
  const next=api.renderLiveDetector();
  assert.equal(el.htmlWrites,firstHtmlWrites,'no DOM rebuild during async refresh');
  assert.equal(el.textWrites,firstTextWrites,'no collapsed loading placeholder during refresh');
  assert.equal(el.innerHTML,firstContent,'old result stays visible until new response');
  finish({...current});
  deferred=null;
  await next;
  assert.equal(el.htmlWrites,firstHtmlWrites,'identical signal keeps DOM intact');
  assert.equal(el.textWrites,firstTextWrites,'result never cleared during interval refresh');

  el.raw.open=true;
  current={...current,return15s:0.42};
  await api.renderLiveDetector();
  assert.equal(el.htmlWrites,firstHtmlWrites+1,'material data change paints once');
  assert.equal(el.raw.open,true,'expanded raw details remains open');
  assert.equal(el.textWrites,firstTextWrites,'changed data also never clears content');

  let endStale;
  deferred=resolve=>{endStale=resolve};
  const stale=api.renderLiveDetector();
  controls.liveDetectorEnabled.checked=false;
  endStale({...current,return15s:0.55});
  await stale;
  assert.equal(el.htmlWrites,firstHtmlWrites+1,'late response discarded after disabling live');
  assert(!el._attributes.has('aria-busy'),'busy flag always cleaned up');
  // Enabling/disabling a tall result above the visible viewport must not
  // shift the visible content below it (even when height changes by 400px).
  let areaHeight=20;browserViewport.scrollY=1200;
  const offscreen={closest(){return this},getBoundingClientRect(){return {bottom:400+areaHeight-browserViewport.scrollY}}};
  const startingBottom=offscreen.getBoundingClientRect().bottom;
  api.mutateDetectorWithoutViewportJump(offscreen,()=>{areaHeight=429});
  assert.equal(browserViewport.scrollY,1609,'starting adjusts scroll for expanded panel');
  assert.equal(offscreen.getBoundingClientRect().bottom,startingBottom,'visible content stays anchored on start');
  api.mutateDetectorWithoutViewportJump(offscreen,()=>{areaHeight=20});
  assert.equal(browserViewport.scrollY,1200,'stopping returns viewport to prior scroll position');
  assert.equal(offscreen.getBoundingClientRect().bottom,startingBottom,'visible content stays anchored on stop');

  console.log('PASS: no content collapse; no redundant re-render; details preserved; stale response ignored; calls='+detectorCalls);
}
test().catch(e=>{console.error(e);process.exitCode=1});
