const assert=require('node:assert/strict');
const fs=require('node:fs');
const s=fs.readFileSync(process.argv[2],'utf8');
const part=s.slice(s.indexOf('function scanThreeGreenBars('),s.indexOf('async function loadThreeGreenOverlay('));
const fakeTime=Date.parse('2026-10-12T01:00:20.000Z'); // 09:00:20 Taipei
class TestDate extends Date{static now(){return fakeTime}}
let checked=true,mirror=false,fetchCount=0,saves=0;
const nodes={
 threeGreenRealtime:{checked:true},block4ThreeGreenRealtime:{checked:false},
 threeGreenLiveRecent:{style:{display:'none'},textContent:''},
 threeGreenLiveHeadline:{textContent:'',title:''},
 threeGreenLiveMeta:{textContent:'',title:''},
 symbol:{value:'1301'},date:{value:'2026-10-12'},
 threeGreenMode:{value:'SLIDING'},threeGreenVersion:{value:'V1'},threeGreenLimit:{value:'30'},
 threeGreenOverlay:{checked:false},multiDay:{checked:false}
};
const $=id=>nodes[id];
const at=n=>new Date(Date.parse('2026-10-12T01:00:00Z')+n*5000).toISOString();
const price=[10,11,12,13,14];
const bars=price.map((close,i)=>({startTime:at(i),close}));
async function fetchBars(){fetchCount++;return{bars,source:'SPARK-live'}}
const api=new Function('$','fetchBars','Date','fmtP','savePrefs','mutateDetectorWithoutViewportJump','threeGreenOverlayState','draw',part+
 '\nreturn {scanThreeGreenBars,refreshThreeGreenRealtime,toggleThreeGreenRealtime,threeGreenRealtimeMarketClock,threeGreenRealtimeState};')(
 $,fetchBars,TestDate,n=>n.toString(),()=>{saves++},(el,fn)=>fn(),{key:'',signals:[]},()=>{});
async function main(){
 let stats={};
 const full=api.scanThreeGreenBars(bars,'SLIDING','V1',30,stats);
 assert.equal(full.length,2,'unfiltered synthetic bars have two overlapping signals');
 assert.equal(api.scanThreeGreenBars(bars,'NON_OVERLAP','V1',30).length,1,'A non-overlap');
 const expired=[{startTime:at(0),close:10},{startTime:at(1),close:11},
 {startTime:at(10),close:12},{startTime:at(11),close:13}];
 assert.equal(api.scanThreeGreenBars(expired,'NON_OVERLAP','V2',30).length,0,'V2 deadline enforced');
 assert.equal(api.threeGreenRealtimeMarketClock().trading,true,'synthetic clock is in session');
 await api.refreshThreeGreenRealtime(true);
 assert.equal(fetchCount,1,'initial fetch once');
 assert.match(nodes.threeGreenLiveHeadline.textContent,/進度 2\/3｜觸發 1 次/,'unfinished 09:00:20 excluded');
 assert.match(nodes.threeGreenLiveMeta.textContent,/盤中每 5 秒更新/);
 assert.equal(nodes.threeGreenLiveRecent.textContent.split('收盤').length-1,1,'one latest event');
 await api.refreshThreeGreenRealtime();
 assert.equal(fetchCount,1,'throttle suppresses duplicate fetch');
 api.toggleThreeGreenRealtime(false);
 assert.equal(nodes.block4ThreeGreenRealtime.checked,false,'mirror OFF');
 assert.equal(nodes.threeGreenLiveRecent.style.display,'none','hidden when OFF');
 assert.match(nodes.threeGreenLiveHeadline.textContent,/關閉/);
 api.toggleThreeGreenRealtime(true);
 assert.equal(nodes.block4ThreeGreenRealtime.checked,true,'mirror ON');
 assert.equal(nodes.threeGreenLiveRecent.style.display,'block','visible when ON');
 for(let k=0;k<10&&api.threeGreenRealtimeState.busy;k++)await new Promise(r=>setTimeout(r,5));
 assert.equal(fetchCount,2,'re-enable refreshes');
 assert.equal(saves,2,'toggle saved twice');
 console.log('PASS: sliding/nonoverlap/V2, completed-only 5s bars, throttling, counts, mirror toggle, hide/show, state persistence');
}
main().catch(e=>{console.error(e);process.exitCode=1});
