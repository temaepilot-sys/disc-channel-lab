const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
module.exports=async({sandbox,vm,renderer,element,tick,gpu,setLive,poll,output})=>{
  const checks=[],check=(v,s)=>{assert(v,s);checks.push(s)};
  const select=(id,value)=>{element(id).value=value;element(id).onchange()};
  const S=vm.runInContext('SparkField',sandbox),A=vm.runInContext('Aurora',sandbox);
  select('visual-style','aurora-spark');await sandbox.lab.play();tick(.8);renderer.draw(sandbox.lab.state.time);
  if(process.env.LAB_SPARK_SHADER_FAILURE==='1'||process.env.LAB_HYBRID_SHADER_FAILURE==='1'){
    check(renderer.visualStyle==='particles','Either combined shader failure restores Particles');check(sandbox.lab.state.playing,'Combined shader failure leaves audio playing');
    check(element('spark-controls').hidden&&element('aurora-controls').hidden&&element('veil-palette-label').hidden,'Fallback hides both sets of unavailable controls');
    console.log('PASS combined shader fallback: '+checks.length);return;
  }
  check(renderer.hasSpark&&renderer.hasVeil&&renderer.spark.pool.count>0&&renderer.aurora.stats.drawCalls>0,'Combined mode draws real Spark and band-driven veil together');
  check(renderer.count===0&&renderer.beamMesh.vertexCount===0,'Combined mode does not run the old particle pool or beams');
  check(!element('spark-controls').hidden&&!element('aurora-controls').hidden&&!element('veil-palette-label').hidden&&element('hybrid-control').hidden,'Both relevant controls are visible; old particle amount is hidden');
  const order=[],veilDraw=renderer.aurora.draw.bind(renderer.aurora),sparkDraw=renderer.spark.draw.bind(renderer.spark);
  renderer.aurora.draw=(...v)=>{order.push('veil');return veilDraw(...v)};renderer.spark.draw=(...v)=>{order.push('spark');return sparkDraw(...v)};
  renderer.draw(sandbox.lab.state.time);check(order.join(',')==='veil,spark','Sparks are drawn after the veil');
  check(Array.from(renderer.spark.colors).every((v,i)=>v===renderer.aurora.bandColors[i]),'Matching uses the exact Spark base hue in every band');
  const spark=renderer.spark,pool=spark.pool,veil=renderer.aurora,colors=Array.from(spark.colors),programs=gpu.programs,buffers=gpu.buffers;
  const active=pool.count;select('aurora-palette','spark');renderer.draw(sandbox.lab.state.time);
  check(renderer.aurora.settings.palette==='spark'&&pool.count===active&&colors.every((v,i)=>v===spark.colors[i]),'Matching palette keeps existing sparks and their colors');
  select('aurora-palette','spectrum');check(renderer.aurora.settings.palette==='spectrum','Original palette remains selectable');
  check(gpu.programs===programs&&gpu.buffers===buffers,'Palette switches allocate no new GPU programs/buffers');
  select('aurora-palette','spark');select('aurora-preset','spatial');check(renderer.auroraSettings.palette==='spark','Veil motion presets retain the independently selected matching palette');
  sandbox.lab.pause();const paused=JSON.stringify(Array.from(veil.motion.packed)),time=sandbox.lab.state.time;tick(.4);
  check(pool.count===active&&JSON.stringify(Array.from(veil.motion.packed))===paused&&sandbox.lab.state.time===time,'Pause freezes sparks, veil and audio time together');
  select('aurora-palette','spark');select('aurora-flow','down');await element('save-settings').onclick();select('visual-style','particles');await element('load-settings').onclick();
  check(renderer.visualStyle==='aurora-spark'&&renderer.auroraSettings.palette==='spark'&&renderer.auroraSettings.flowDirection==='down','Combined mode, matching and flow survive Save/Load');
  for(let i=0;i<6;i++){select('visual-style','hybrid');select('visual-style','spark-field');select('visual-style','aurora-spark');renderer.draw(time)}
  check(renderer.spark===spark&&renderer.aurora===veil&&gpu.programs===programs&&gpu.buffers===buffers,'Repeated mode changes reuse both GPU renderers');
  const codes=['FL','FR','FC','LFE','SL','SR'],n=codes.length*16;
  const payload={configured:true,connected:true,playing:true,hasData:true,revision:1,epoch:1,position:10,duration:100,name:'Combined regression',codec:'PCM',sourceSampleRate:48000,channels:codes,bandCount:16,centers:Array.from({length:16},(_,i)=>20*1000**((i+.5)/16)),input:new Array(n).fill(.7),post:new Array(n).fill(.65),output:new Array(32).fill(.6),fastInput:new Array(n).fill(.8),fastPost:new Array(n).fill(.7),fastOutput:new Array(32).fill(.7),speakerStates:codes.map(code=>({code,enabled:true}))};
  setLive(payload);await poll();tick(.15);check(renderer.spark.pool.count>0&&renderer.aurora.motion.packed.some(v=>v!==0),'Both effects follow live current audio');
  payload.speakerStates[2].enabled=false;setLive(payload);await poll();renderer.draw(10);
  check(renderer.spark.stats.perChannel[2].active===0&&renderer.aurora.motion.packets[2].every(v=>v===0),'Mute/Solo exclusion clears both effects immediately');
  select('analysis-mode','output');await poll();tick(.1);check(renderer.speakers.length===2&&renderer.aurora.motion.channels===2&&renderer.spark.speakers.length===2,'Final stereo reconfigures both engines to two actual outputs');
  select('analysis-mode','input');await poll();tick(.1);check(renderer.speakers.length===6&&renderer.spark.stats.perChannel[2].active===0,'Input returns to source layout while respecting exclusion');
  payload.epoch++;payload.position=0;payload.playing=false;setLive(payload);await poll();check(pool.count===0&&veil.motion.packed.every(v=>v===0),'Seek/Stop clears both afterglows');
  check(A.style('aurora-spark')==='aurora-spark'&&A.sanitize({palette:'unknown'}).palette==='spectrum','Mode and palette migration are safe');
  fs.mkdirSync(path.join(output,'aurora-spark'),{recursive:true});fs.writeFileSync(path.join(output,'aurora-spark/logic-results.json'),JSON.stringify({checks},null,2));console.log('PASS Aurora + Spark: '+checks.length+' checks');
};
