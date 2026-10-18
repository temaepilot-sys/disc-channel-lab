const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const context=vm.createContext({Float32Array,Math,Number,Object,Array,performance});
vm.runInContext(fs.readFileSync('DiscChannelLab.Visualizer/Web/spectral-veil.js','utf8'),context);
const A=vm.runInContext('Aurora',context),checks=[];
function check(v,label){assert(v,label);checks.push(label)}
const info={bandCount:16,centers:Array.from({length:16},(_,b)=>20*1000**((b+.5)/16))};
const values=new Float32Array(96),zero=new Float32Array(96),settings=A.sanitize();
const motion=new A.BandMotion(6,info),run=(v,n=90)=>{for(let i=0;i<n;i++)for(let c=0;c<6;c++)motion.updateChannel(c,v,v,1/60,settings)};
run(zero);check(motion.packed.every(v=>v===0),'Silence creates no light or movement');
values[2*16+9]=.8;run(values);
check(motion.count===16&&motion.packed[2*128+9*4]>.79,'All 16 bands reach the GPU independently');
check(motion.vertical[2*16+9]!==0,'Audible band drives actual vertical deformation');
check(motion.packed.every((v,i)=>Math.floor(i/128)===2&&Math.floor((i%128)/4)===9||v===0),'A channel/band never energizes its neighbors or other channels');
const original=motion.vertical[41];run(values,5);check(original!==motion.vertical[41],'A sustained audible band vibrates');
const low=new A.BandMotion(1,info),high=new A.BandMotion(1,info);
check(low.rates[2]<high.rates[13],'Higher frequencies have faster visual vibration rates');
values.fill(.9);run(values,90);const packet=Array.from(motion.packed);motion.hide(2);
check(motion.packets[2].every(v=>v===0),'Mute immediately removes that channel’s residual motion and glow');
check(Array.from(motion.packets[1]).every((v,i)=>v===packet[128+i]),'Mute leaves other source bands unchanged');
run(zero,900);check(motion.packed.every(v=>v===0)&&motion.vertical.every(v=>v===0),'Silence completely settles afterglow and deformation');
const phases=Array.from(motion.phase);run(zero,60);check(Array.from(motion.phase).every((v,i)=>v===phases[i]),'Silent bands have no free-running animation phase');
function rms(level){const m=new A.BandMotion(1,info),v=new Float32Array(16);v[8]=level;let sum=0;
  for(let i=0;i<240;i++){m.updateChannel(0,v,v,1/60,settings);if(i>59)sum+=m.vertical[8]**2}return Math.sqrt(sum/180)}
check(rms(.9)>rms(.3)*3,'Strong bands move substantially more than weak bands');
check(A.style('aurora')==='particles'&&A.style('electric-flow')==='particles'&&A.style('hybrid')==='hybrid','Retired saved modes safely fall back; revised Hybrid remains');
motion.clear();check(motion.packed.every(v=>v===0),'Source/seek reset clears all packets');
values.fill(NaN);run(values);check(motion.packed.every(Number.isFinite),'Malformed analysis never uploads nonfinite uniforms');
for(const speed of [0,2]){settings.speed=speed;values.fill(1);run(values);check(motion.packed.every(Number.isFinite),'Extreme speed '+speed+' remains finite');if(speed===0)check(motion.vertical.every(v=>v===0),'Zero speed settles the band displacement')}
const first=fs.readFileSync('DiscChannelLab.Visualizer/Web/spectral-veil.js','utf8');
check(!first.includes('uTime'),'No global time uniform can drive unrelated curtain waves');
for(const direction of ['none','down','up']){
  const m=new A.BandMotion(2,info),v=new Float32Array(32);v[8]=.8;const s=A.sanitize({flowDirection:direction});
  for(let i=0;i<90;i++){m.updateChannel(0,v,v,1/60,s);m.updateChannel(1,v,v,1/60,s)}
  check(direction==='none'?m.flowPhase[0]===0:m.flowPhase[0]>0,'Flow '+direction+' respects the switch and audible energy');
  check(m.flowPhase[1]===0,'Silent channel has no independent flow in '+direction);
  const before=m.flowPhase[0];m.updateChannel(0,v,v,0,s);check(before===m.flowPhase[0],'Paused time freezes '+direction+' flow');
  m.hide(0);check(m.flowPhase[0]===0,'Mute/source reset clears '+direction+' flow');
}
check(A.sanitize({flowDirection:'sideways'}).flowDirection==='none','Invalid flow safely falls back to Off');
fs.mkdirSync('verification-output/space-sketch',{recursive:true});
fs.writeFileSync('verification-output/space-sketch/band-motion-results.json',JSON.stringify({checks},null,2));console.log('PASS Hybrid band motion: '+checks.length);
