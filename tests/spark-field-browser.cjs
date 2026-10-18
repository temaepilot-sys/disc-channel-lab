// Real installed-browser/WebGL + published host + actual named-pipe PCM integration.
// No browser packages are installed. Start an isolated Chromium debugging profile first.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),os=require('node:os');
const {spawn}=require('node:child_process');
const {connect}=require('./helpers/cdp.cjs');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const executable=path.resolve(process.argv[2]||require('./helpers/environment.cjs').viewer());
const output=path.resolve(process.argv[3]||'verification-output/space-sketch/spark-browser');
if(executable.includes('playback-fixed-v2'))throw Error('The protected v2 build must not be used for this test.');
fs.mkdirSync(output,{recursive:true});
const checks=[],images=[],measures=[];
function check(value,label){if(!value)throw Error(label);checks.push(label);console.log('PASS '+label);}
const control={channels:['FL','FR'],paused:false,mute:new Set(),solo:new Set(),head:null,sample:0,epoch:11,silence:false};
let socket,feeding=false,timer,client,host;
const pipe='DiscChannelLab.SpaceSketch.v1.SparkTest'+process.pid;
const server=net.createServer(s=>{socket=s;s.on('error',()=>{});s.on('close',()=>{if(socket===s)socket=null;});});
function write(payload){if(!socket||socket.destroyed)return;const size=Buffer.alloc(4);size.writeInt32LE(payload.length);socket.write(Buffer.concat([size,payload]));}
function feed(){
  if(!feeding||!socket)return;
  const codes=control.channels,n=codes.length,count=960;
  const enabled=c=>!control.mute.has(c)&&(!control.solo.size||control.solo.has(c));
  const clock={Version:1,Epoch:control.epoch,Name:`Browser integration · ${n}ch generated PCM`,Codec:'PCM test fixture',SourceSampleRate:48000,
    Position:control.sample/48000,Duration:1000,Playing:!control.paused,ClockMilliseconds:Math.floor(os.uptime()*1000),
    SpeakerStates:codes.map(Code=>({Code,Muted:control.mute.has(Code),Solo:control.solo.has(Code),Enabled:enabled(Code)}))};
  if(control.head){clock.Head=control.head;clock.HeadphoneRenderer='Direct (test pose)';clock.Scene={RotateWorld:true,FrontAngle:30,Speakers:codes.map(Code=>{const a=({FL:-30,FR:30,FC:0,LFE:0,SL:-110,SR:110,BL:-150,BR:150}[Code]||0)*Math.PI/180;return{Code,X:Math.sin(a),Y:0,Z:Math.cos(a)}})};}
  write(Buffer.concat([Buffer.from([1]),Buffer.from(JSON.stringify(clock))]));
  if(control.paused)return;
  const names=Buffer.from(codes.join(',')),header=1+4+8+1+names.length+4;
  const p=Buffer.alloc(header+count*(n*6+4));p[0]=2;p.writeInt32LE(control.epoch,1);p.writeDoubleLE(control.sample/48000,5);p[13]=names.length;names.copy(p,14);p.writeInt32LE(count,14+names.length);
  const postStart=header+count*n*2,outStart=postStart+count*n*4;
  for(let f=0;f<count;f++){
    let l=0,r=0;
    for(let c=0;c<n;c++){
      const t=(control.sample+f)/48000,code=codes[c],amplitude=.6+.4*Math.sin(t*.8+c)**2;
      const v=control.silence?0:code==='LFE'?.15*Math.sin(2*Math.PI*60*t):amplitude*(.11*Math.sin(2*Math.PI*(220+c*35)*t)+.06*Math.sin(2*Math.PI*900*t)+.04*Math.sin(2*Math.PI*4000*t)+.025*Math.sin(2*Math.PI*12000*t));
      const post=enabled(code)?v*.5:0;
      p.writeInt16LE(Math.round(v*32767),header+(f*n+c)*2);p.writeFloatLE(post,postStart+(f*n+c)*4);
      if(['FL','SL','BL'].includes(code))l+=post/n;else if(['FR','SR','BR'].includes(code))r+=post/n;else{l+=post/n;r+=post/n;}
    }
    p.writeInt16LE(Math.round(l*32767),outStart+f*4);p.writeInt16LE(Math.round(r*32767),outStart+f*4+2);
  }
  write(p);control.sample+=count;
}
const hashExpression=`(()=>{const p=renderer.spark.pool, a=new Uint32Array(p.data.buffer,0,p.count*SparkField.STRIDE);let h=2166136261;for(let i=0;i<a.length;i++)h=Math.imul(h^a[i],16777619);return {hash:h,count:p.count,time:lab.state.time}})()`;
async function waitFor(expression,label,timeout=7000){const end=Date.now()+timeout;while(Date.now()<end){if(await client.evaluate(expression))return;await delay(40);}throw Error('Timed out: '+label);}
async function capture(name){const data=await client.evaluate('({settings:captureSettings(),state:lab.state,diagnostics:lab.diagnostics,camera:renderer.camera,glError:renderer.gl.getError()})');await client.screenshot(path.join(output,name+'.png'));images.push({file:name+'.png',resolution:[1920,1080],...data});return data;}
async function sampling(milliseconds){
  await client.evaluate(`window.__sparkSamples=[];window.__sparkSample=true;(function sample(){if(!window.__sparkSample)return;const s=renderer.spark.stats;window.__sparkSamples.push([s.updateMs,s.drawCpuMs,s.particles]);requestAnimationFrame(sample)})()`);
  await delay(milliseconds);
  return client.evaluate(`(()=>{window.__sparkSample=false;const a=window.__sparkSamples;const percentile=(i,p)=>{const b=a.map(v=>v[i]).sort((x,y)=>x-y);return b[Math.floor((b.length-1)*p)]||0};return {samples:a.length,updateCpuP95Ms:percentile(0,.95),drawCpuP95Ms:percentile(1,.95),activeParticlesP95:percentile(2,.95),diagnostics:lab.diagnostics}})()`);
}
async function main(){
  await new Promise((resolve,reject)=>{server.on('error',reject);server.listen('\\\\.\\pipe\\'+pipe,resolve)});
  host=spawn(executable,['--headless','--live-pipe',pipe],{windowsHide:true,stdio:['ignore',fs.openSync(path.join(output,'host.stdout.log'),'w'),fs.openSync(path.join(output,'host.stderr.log'),'w')]});
  const endpoint=path.join(path.dirname(executable),'cache','endpoint.json');
  for(let i=0;i<100;i++){try{if(JSON.parse(fs.readFileSync(endpoint)).pid===host.pid)break}catch{}await delay(100);}
  client=await connect(endpoint,Number(process.env.SPARK_CDP_PORT||9236));
  await waitFor('liveInitialized && liveMode','initial link handshake before explicit demo selection');
  check(!(await client.evaluate('lab.state.playing')),'Startup does not autoplay');
  const gpu=await client.evaluate(`(()=>{const g=renderer.gl,e=g.getExtension('WEBGL_debug_renderer_info');return{renderer:e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER),version:g.getParameter(g.VERSION),pointSizeRange:Array.from(g.getParameter(g.ALIASED_POINT_SIZE_RANGE)),browser:navigator.userAgent}})()`);
  await client.evaluate(`window.__gpu={buffers:0,programs:0};for(const [method,key] of [['createBuffer','buffers'],['createProgram','programs']]){const old=renderer.gl[method].bind(renderer.gl);renderer.gl[method]=(...args)=>{window.__gpu[key]++;return old(...args)}};$('volume').value=0;$('volume').oninput();$('visual-style').value='spark-field';$('visual-style').onchange();renderer.camera.distance=8.5;renderer.camera.pitch=.3;$('grid').checked=false;renderer.buildScene();load('/api/demo')`);
  check(await client.evaluate("renderer.visualStyle==='spark-field' && renderer.spark.stats.buffers===1 && renderer.spark.stats.programs===1"),'Actual Spark shaders compile and link on the GPU');
  await client.evaluate(`applySparkControls({...SparkField.defaults,afterglow:0});$('lfe').checked=false;lab.seek(5).then(()=>lab.play())`);await delay(2500);
  const phase1=await capture('phase1-micro-sparks-and-streaks');
  check(phase1.diagnostics.sparkField.micro>0&&phase1.diagnostics.sparkField.streaks>0&&phase1.diagnostics.sparkField.motes===0,'Phase 1: real WebGL draws sparks and short streaks without motes or legacy effects');
  for(const preset of ['calm','tesla','surround']){
    await client.evaluate(`lab.pause();$('lfe').checked=true;applySparkControls(SparkField.presets.${preset});renderer.clear();lab.seek(5).then(()=>lab.play())`);
    const stats=await sampling(3200);measures.push({scenario:'5.1 demo',preset,...stats});
    const image=await capture(preset==='calm'?'calm-fireflies':preset==='tesla'?'tesla-dust':'surround-sparks');
    check(image.glError===0&&image.diagnostics.sparkField.drawCalls===1&&image.diagnostics.sparkField.motes>0&&image.diagnostics.aurora.drawCalls===0&&image.diagnostics.electricFlow.drawCalls===0,'Phase 2: '+preset+' has real motes, one Spark draw, no legacy effects and no GL error');
  }
  await client.evaluate('lab.pause()');const paused=await client.evaluate(hashExpression);await delay(400);const held=await client.evaluate(hashExpression);
  check(paused.hash===held.hash&&paused.time===held.time,'Actual browser pause freezes particle motion and the audio clock');
  await client.evaluate('lab.seek(20)');check(await client.evaluate('renderer.spark.pool.count===0'),'Actual browser seek immediately clears afterglow');
  await client.evaluate(`applySparkControls({...SparkField.presets.tesla,speed:1.2,streakLength:1.45,twinkle:.25});settingsAction(true)`);
  await client.evaluate(`applySparkControls(SparkField.presets.calm);settingsAction(false)`);
  check(await client.evaluate('renderer.sparkSettings.preset===\'tesla\' && renderer.sparkSettings.speed===1.2 && renderer.sparkSettings.streakLength===1.45 && renderer.sparkSettings.twinkle===.25'),'Published HTTP preferences round-trip Spark settings');
  const resources=await client.evaluate('({...window.__gpu})');
  await client.evaluate(`for(let i=0;i<40;i++){renderer.setVisualStyle('particles');renderer.setVisualStyle('spark-field')}renderer.draw(lab.state.time)`);
  check(JSON.stringify(resources)===JSON.stringify(await client.evaluate('({...window.__gpu})'))&&await client.evaluate('renderer.gl.getError()===0'),'Repeated real-GPU mode changes reuse resources without GL errors');
  feeding=true;timer=setInterval(feed,20);feed();await client.evaluate("$('follow').onclick()");
  await waitFor('lab.state.liveMode && lab.state.channels.length===2 && renderer.spark.pool.count>0','real stereo IPC/FFT');
  check(await client.evaluate("renderer.spark.speakers.map(s=>s.code).join(',')==='FL,FR'"),'Actual PCM pipe + FFT produces only FL/FR for stereo');
  await client.evaluate("$('analysis-mode').value='input';$('analysis-mode').onchange()");
  await delay(1000);await capture('stereo-input');
  for(const mode of ['input','post']){
    await client.evaluate(`$('analysis-mode').value='${mode}';$('analysis-mode').onchange()`);await delay(500);
    const level=await client.evaluate('Array.from(renderer.spark.features.energy).reduce((a,b)=>a+b,0)/renderer.spark.features.energy.length');
    measures.push({scenario:'stereo IPC',signal:mode,meanNormalizedBand:level});
  }
  const levels=measures.filter(m=>m.scenario==='stereo IPC');check(levels[0].meanNormalizedBand>levels[1].meanNormalizedBand+.02,'Real FFT distinguishes original Input from attenuated Post');
  control.channels=['FL','FR','FC','LFE','SL','SR'];control.epoch++;control.sample=0;
  await waitFor('lab.state.channels.length===6 && renderer.spark.pool.count>100','5.1 IPC');
  control.mute.add('FC');await waitFor("renderer.spark.stats.perChannel.find(c=>c.code==='FC')?.active===0 && renderer.speakers.find(s=>s.code==='FC')?.enabled===false",'real mute');
  check(true,'Real IPC Mute immediately removes the channel light');
  control.mute.clear();control.solo.add('LFE');await delay(300);
  check(await client.evaluate("renderer.spark.stats.perChannel.every(c=>c.code==='LFE'||c.active===0) && renderer.spark.stats.perChannel.find(c=>c.code==='LFE').active>0"),'Real IPC Solo preserves only LFE floor motes');control.solo.clear();
  await client.evaluate("$('analysis-mode').value='output';$('analysis-mode').onchange()");await delay(400);
  check(await client.evaluate("renderer.spark.speakers.map(s=>s.code).join(',')==='FL,FR' && renderer.spark.pool.count>0"),'Real final stereo has no invented Center, surround or LFE emitter');
  await client.evaluate("$('analysis-mode').value='post';$('analysis-mode').onchange()");
  control.channels=['FL','FR','FC','LFE','BL','BR','SL','SR'];control.epoch++;control.sample=0;
  await waitFor('renderer.spark.speakers.length===8 && renderer.spark.pool.count>100','7.1 IPC');
  check(await client.evaluate('renderer.spark.stats.perChannel.every(c=>c.active>0)'),'All eight real 7.1 channels receive particles');
  control.head={Yaw:65,Pitch:15,Roll:5};await delay(300);
  check(await client.evaluate('!!lab.head && !!lab.scene.rotateWorld && renderer.matrix.every(Number.isFinite) && renderer.gl.getError()===0'),'Real head pose rotates the Spark scene without invalid transforms');
  for(const view of ['orbit','bird','top','listener','front','back']){await client.evaluate(`$('view').value='${view}';$('view').onchange();renderer.draw(lab.state.time)`);check(await client.evaluate('renderer.matrix.every(Number.isFinite)&&renderer.gl.getError()===0'),'Actual GPU camera '+view+' remains valid');}
  if(process.env.LAB_HYBRID_IPC==='1'){
    control.head=null;
    await client.evaluate("$('visual-style').value='hybrid';$('visual-style').onchange();applyAuroraControls(Aurora.presets.spatial)");
    await waitFor('renderer.aurora?.motion.packets.length===8 && renderer.aurora.motion.packets.every(p=>p.some(v=>v!==0))','Hybrid real 7.1 PCM bands');
    check(await client.evaluate('renderer.aurora.stats.bandsPerChannel===16 && renderer.aurora.stats.drawCalls>0 && renderer.gl.getError()===0'),'Revised Hybrid receives all 16 FFT bands in all eight real channels');
    control.mute.add('FL');await delay(250);
    check(await client.evaluate('renderer.aurora.motion.packets[0].every(v=>v===0) && renderer.aurora.motion.packets[1].some(v=>v!==0)'),'Hybrid real Mute clears only the muted source’s motion');
    control.mute.clear();control.solo.add('LFE');await delay(250);
    check(await client.evaluate('renderer.aurora.items.every(i=>i.speaker.code===\'LFE\'||renderer.aurora.motion.packets[i.channel].every(v=>v===0)) && renderer.aurora.motion.packets[3].some(v=>v!==0)'),'Hybrid real Solo leaves only the LFE field');
    control.solo.clear();control.paused=true;await waitFor('!lab.state.playing','Hybrid linked pause');
    const held=await client.evaluate('JSON.stringify(Array.from(renderer.aurora.motion.packed))');await delay(220);
    check(held===await client.evaluate('JSON.stringify(Array.from(renderer.aurora.motion.packed))'),'Hybrid linked pause freezes motion');
    control.epoch++;control.sample+=480000;await delay(200);
    check(await client.evaluate('renderer.aurora.motion.packed.every(v=>v===0)'),'Hybrid linked seek clears all previous vibration');
    control.paused=false;await delay(500);
    await client.evaluate("$('view').value='orbit';$('view').onchange();renderer.camera.distance=8.5");await delay(200);
    await capture('hybrid-real-7-1-pcm');
    await client.evaluate("$('analysis-mode').value='output';$('analysis-mode').onchange()");await delay(400);
    check(await client.evaluate("renderer.aurora.items.map(i=>i.speaker.code).join(',')==='FL,FR' && renderer.gl.getError()===0"),'Hybrid final stereo keeps explicit FL / FR mapping');
    await client.evaluate("$('analysis-mode').value='post';$('analysis-mode').onchange();$('visual-style').value='spark-field';$('visual-style').onchange()");await delay(350);
  }
  control.paused=true;await waitFor('!lab.state.playing','live pause');const hold=await client.evaluate(hashExpression);await delay(250);check((await client.evaluate(hashExpression)).hash===hold.hash,'Real linked pause freezes the Spark pool');
  control.epoch++;control.sample+=480000;await delay(200);check(await client.evaluate('renderer.spark.pool.count===0'),'Real linked seek removes all previous light');
  control.paused=false;control.head=null;await delay(300);control.silence=true;await delay(3500);
  check(await client.evaluate('renderer.spark.pool.count===0'),'Real silence expires every particle without a minimum background emission');
  feeding=false;socket?.destroy();await delay(400);check(await client.evaluate('renderer.spark.pool.count===0&&!lab.state.playing'),'Disconnected pipe stops and clears the view');
  await client.evaluate(`$('volume').value=0;$('volume').oninput();load('/api/demo',true)`);await delay(250);
  const time=await client.evaluate('lab.state.time');const available=await client.evaluate("(()=>{const e=renderer.gl.getExtension('WEBGL_lose_context');if(e){e.loseContext();return true}return false})()");await delay(600);
  check(available&&await client.evaluate(`lab.state.playing&&lab.state.time>${time+.2}&&renderer.contextLost`),'WebGL context loss leaves the independent demo audio clock running');
  fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({kind:'Actual browser WebGL / installed GPU / real PCM IPC and FFT',checks,gpu,images,measures,events:client.events},null,2));
  console.log(JSON.stringify({passed:checks.length,gpu:gpu.renderer,images:images.map(i=>i.file),measures},null,2));
}
main().catch(e=>{console.error(e);process.exitCode=1;fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({error:e.stack,checks,images,measures,events:client?.events},null,2));}).finally(async()=>{
  clearInterval(timer);feeding=false;socket?.destroy();server.close();
  if(client){try{await client.evaluate('lab.pause();api(\'/api/quit\')')}catch{}client.close();}
  if(host){await delay(700);if(host.exitCode===null)host.kill();}
  const preferences=path.join(path.dirname(executable),'ViewerSettings.json');
  if(fs.existsSync(preferences))fs.renameSync(preferences,path.join(output,'tested-ViewerSettings.json'));
});
