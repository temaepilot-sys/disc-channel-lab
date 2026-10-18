// Mock WebGL checks: logic/resource tests only. Actual images/timing are tested separately.
const assert = require('node:assert/strict');
const fs = require('node:fs'); const path = require('node:path');
module.exports = async ({sandbox, vm, renderer, element, tick, gl, gpu, setLive, poll, output}) => {
  const S = vm.runInContext('SparkField', sandbox), checks = [];
  function check(ok, label) { assert(ok, label); checks.push(label); }
  const select = (id,value) => { element(id).value=value; element(id).onchange(); };
  const adjust = (id,value) => { element(id).value=value; element(id).oninput(); };
  select('visual-style','spark-field');
  if (process.env.LAB_SPARK_SHADER_FAILURE === '1') {
    check(renderer.visualStyle==='particles' && !renderer.spark,'Shader failure falls back to Particles');
    await sandbox.lab.play();tick(.25);check(sandbox.lab.state.playing,'Audio remains playable after Spark shader failure');
    fs.writeFileSync(path.join(output,'spark-field-shader-fallback.json'),JSON.stringify({checks},null,2));
    console.log('PASS Spark shader fallback: '+checks.length);return;
  }
  check(renderer.visualStyle==='spark-field' && renderer.spark,'Spark Field is an independent selectable mode');
  check(S.sanitize({density:Infinity,intensity:NaN,spread:-1,quality:'ultra',preset:'bad'}).density===1 && S.sanitize({spread:-1}).spread===0,'Nonfinite/unknown options default and numeric bounds clamp');
  check(S.power(0)===0 && Math.abs((S.power(1)/S.power(1-6.0206/70))-4)<.001,'Band energy is converted to power, not summed as dB; true zero stays zero');
  const poolA=new S.Pool(12345),poolB=new S.Pool(12345);
  check(Array.from({length:100},()=>poolA.random()===poolB.random()).every(Boolean),'A fixed random seed gives reproducible particle events');
  const info={channels:['FL','FR','FC','LFE','SL','SR'],bandCount:16,centers:Array.from({length:16},(_,i)=>20*1000**((i+.5)/16))};
  const positions=[[-1.75,1.16,-3.03],[1.75,1.16,-3.03],[0,1.16,-3.5],[0,.12,0],[-3.28,1.16,1.2],[3.28,1.16,1.2]];
  const speakers=info.channels.map((code,i)=>({code,position:positions[i],radius:Math.hypot(positions[i][0],positions[i][2]),azimuth:Math.atan2(-positions[i][2],-positions[i][0])}));
  let suppressed=-1;
  const scene={canvas:{height:1080},showSparkLfe:true,channelSuppressed:c=>c===suppressed};
  const energy=new Float32Array(96).fill(.65),zero=new Float32Array(96);
  function engine(hz=60,seconds=3,settings={}) {
    const r=new S.Renderer(gl,7291);r.configure(speakers,info,{minimumDb:-70,maximumDb:0});r.setOptions({...S.defaults,...settings});
    for(let i=0;i<Math.round(hz*seconds);i++)r.update(energy,energy,1/hz,(i+1)/hz,scene);
    return r;
  }
  const r=engine();
  const vx=r.pool.data[3],x=r.pool.data[0];r.setOptions({...S.defaults,speed:2});
  check(r.pool.data[0]===x&&Math.abs(r.pool.data[3]-vx*2)<1e-6,'Motion speed adjusts current velocity without moving particles or changing the audio clock');
  r.setOptions(S.defaults);
  check(r.stats.micro>0 && r.stats.streaks>0 && r.stats.motes>0,'Micro Sparks, Short Streaks and Floating Glow Motes coexist');
  check(r.pool.count<=8000 && r.stats.perChannel.every(c=>c.active>0),'All real channels receive fair particle capacity');
  check(r.pool.data.subarray(0,r.pool.count*S.STRIDE).every(Number.isFinite),'All active positions/velocities/ages/colors remain finite');
  let floor=true,heightMin=Infinity,heightMax=-Infinity,zMin=Infinity,zMax=-Infinity;
  for(let p=0;p<r.pool.count;p++){const i=p*S.STRIDE,a=r.pool.data; if(a[i+15]===3) floor&&=a[i+14]===2&&a[i+1]<=.32&&a[i+8]>.9;else{heightMin=Math.min(heightMin,a[i+1]);heightMax=Math.max(heightMax,a[i+1]);zMin=Math.min(zMin,a[i+2]);zMax=Math.max(zMax,a[i+2]);}}
  check(floor,'Only a real LFE emits warm floor motes');check(heightMax-heightMin>2 && zMax-zMin>4,'The field has real vertical and depth variation, not three thin planes');
  const before=Array.from(r.pool.data.subarray(0,r.pool.count*S.STRIDE)),emitted=r.stats.emitted;
  r.update(energy,energy,0,3,scene);check(before.every((v,i)=>v===r.pool.data[i]) && r.stats.emitted===emitted,'Zero audio delta freezes motion, lifetimes and emission');
  suppressed=2;r.hide(2);check(r.stats.perChannel[2].active===0,'Mute/Solo exclusion immediately purges existing channel particles');
  r.update(energy,energy,.02,3.02,scene);check(r.stats.perChannel[2].active===0,'Suppressed channels cannot emit again');suppressed=-1;
  const noNew=r.stats.emitted;for(let i=0;i<240;i++)r.update(zero,zero,1/60,4+i/60,scene);
  check(r.stats.emitted===noNew && r.pool.count===0,'True silence adds zero particles and every afterglow expires');
  r.clear();r.update(energy,energy,60,60,scene);check(r.stats.emitted<1000,'A long stall is clamped instead of causing an unbounded burst');
  const rates=[30,60,120].map(hz=>{const e=engine(hz,4);const count=e.stats.emitted;e.dispose();return count;});
  check(Math.max(...rates)/Math.min(...rates)<1.08,'30/60/120 Hz emission rates agree within 8 percent');
  r.clear();for(let i=0;i<20000;i++)r.spawn(0,10,1,1);
  check(r.pool.count<=r.reserve+r.sharedLimit && r.pool.count<8000,'A loud single channel cannot occupy the entire pool');
  for(let c=1;c<6;c++)for(let i=0;i<r.reserve;i++)r.spawn(c,10,1,1);
  check(r.pool.count===8000 && r.stats.dropped>0,'Reserved and shared slots obey the exact quality cap');
  for(const quality of ['low','medium','high']){r.setOptions({...S.defaults,quality});check(r.pool.count<=S.limits[quality],quality+' capacity is enforced immediately');}
  const allocations=gpu.buffers,programs=gpu.programs,buffer=r.buffer,identity=new Float32Array([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]);
  gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.blendFuncSeparate(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA,gl.ONE,gl.ZERO);
  const oldProgram=gl.getParameter(gl.CURRENT_PROGRAM),oldBuffer=gl.getParameter(gl.ARRAY_BUFFER_BINDING);
  for(let i=0;i<40;i++)r.draw(identity,scene);
  check(r.stats.drawCalls===1 && gpu.buffers===allocations && gpu.programs===programs && r.buffer===buffer,'All three types use one draw and reuse the existing program/buffer');
  check(gl.isEnabled(gl.DEPTH_TEST) && gl.getParameter(gl.DEPTH_WRITEMASK)===true && gl.getParameter(gl.CURRENT_PROGRAM)===oldProgram && gl.getParameter(gl.ARRAY_BUFFER_BINDING)===oldBuffer && gl.getParameter(gl.BLEND_SRC_ALPHA)===gl.ONE,'Draw restores program, array buffer, depth and separate blend state');
  const deleted=gpu.deletedBuffers;r.dispose();r.dispose();check(gpu.deletedBuffers===deleted+1,'Dispose releases its buffer exactly once');
  const spark=renderer.spark,buffers=gpu.buffers;await sandbox.lab.play();tick(.5);
  const saved=renderer.spark.stats.emitted;sandbox.lab.pause();tick(.3);check(renderer.spark.stats.emitted===saved,'Application pause freezes the actual Spark renderer');
  await sandbox.lab.seek(2);check(renderer.spark.pool.count===0,'Seek clears old Spark afterglow');
  for(let i=0;i<50;i++){select('visual-style','particles');select('visual-style','spark-field');}
  check(renderer.spark===spark && gpu.buffers===buffers,'Repeated mode changes neither recreate resources nor retain old light');
  select('visual-style','particles');const dormant=renderer.spark.stats.emitted;await sandbox.lab.play();tick(.3);
  check(renderer.spark.stats.emitted===dormant && renderer.count>0,'Particles retains its own engine and Spark performs no inactive updates/draws');
  sandbox.lab.pause();select('visual-style','spark-field');select('spark-preset','tesla');adjust('spark-density',1.7);adjust('spark-length',1.5);adjust('spark-twinkle',.25);
  await element('save-settings').onclick();select('visual-style','particles');await element('load-settings').onclick();
  check(renderer.visualStyle==='spark-field' && renderer.sparkSettings.density===1.7 && renderer.sparkSettings.streakLength===1.5 && renderer.sparkSettings.twinkle===.25,'Spark style and independent options survive save/load');
  await sandbox.lab.seek(39.9);await sandbox.lab.play();vm.runInContext('source.onended()',sandbox);tick(.35);
  check(!sandbox.lab.state.playing&&renderer.spark.pool.count===0,'Natural demo end leaves no frozen afterglow');
  for(const codes of [['FL','FR'],['FL','FR','FC','LFE','SL','SR'],['FL','FR','FC','LFE','BL','BR','SL','SR'],['FC'],['FL','FR','FLC','FRC','SL','SR']]){
    const n=codes.length*16;const payload={configured:true,connected:true,playing:true,hasData:true,revision:1,epoch:1,position:10,duration:100,name:'Spark regression',codec:'PCM',sourceSampleRate:48000,channels:codes,bandCount:16,centers:info.centers,dispersion:new Array(16).fill(30),input:new Array(n).fill(.65),post:new Array(n).fill(.5),output:new Array(32).fill(.6),fastInput:new Array(n).fill(.8),fastPost:new Array(n).fill(.7),fastOutput:new Array(32).fill(.75),speakerStates:codes.map(code=>({code,enabled:true}))};
    setLive(payload);await poll();select('analysis-mode','input');tick(.2);await poll();tick(.15);
    check(renderer.spark.speakers.map(s=>s.code).join(',')===codes.join(',') && renderer.spark.pool.count>0, codes.length+'ch explicit layout has correct live emitters');
    payload.speakerStates[0].enabled=false;setLive(payload);await poll();renderer.draw(10);
    check(renderer.spark.stats.perChannel[0].active===0,'Input visibility immediately follows speaker exclusion for '+codes.join('/'));
    select('analysis-mode','post');await poll();tick(.1);check(renderer.spark.stats.perChannel[0].active===0,'Post visibility follows the same speaker exclusion');
    select('analysis-mode','output');await poll();tick(.15);check(renderer.spark.speakers.map(s=>s.code).join(',')==='FL,FR' && renderer.spark.pool.count>0,'Final stereo has only the two real output channels');
    for(const view of ['orbit','bird','top','listener','front','back']){select('view',view);renderer.draw(10);check(renderer.matrix.every(Number.isFinite),view+' Spark camera matrix remains finite');}
    payload.position=.04;payload.epoch++;setLive(payload);await poll();tick(.08);
    payload.position=0;payload.epoch++;payload.playing=false;setLive(payload);await poll();
    check(renderer.spark.pool.count===0,'Stop within the first 0.2 seconds also clears every spark');
    payload.connected=false;setLive(payload);await poll();check(renderer.spark.pool.count===0,'Disconnect immediately clears Spark afterglow');
  }
  fs.writeFileSync(path.join(output,'spark-field-results.json'),JSON.stringify({mode:'Simulated APIs only; real WebGL screenshots and timings are separate',checks,emissionAt30_60_120Hz:rates},null,2));
  console.log('PASS Spark Field: '+checks.length+' checks');
};
