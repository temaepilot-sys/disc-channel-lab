// Published host, actual WebGL and real IPC. Isolated browser profile only.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),os=require('node:os');
const {spawn}=require('node:child_process'),{Cdp,connect}=require('./helpers/cdp.cjs');
const delay=ms=>new Promise(r=>setTimeout(r,ms)),port=9272;
const exe=require('./helpers/environment.cjs').viewer();
const out=path.resolve(process.env.AURORA_SPARK_BROWSER_OUT||'verification-output/space-sketch/aurora-spark');fs.mkdirSync(out,{recursive:true});
const pipe='DiscChannelLab.SpaceSketch.v1.AuroraSpark'+process.pid,checks=[],captures=[],performanceResults=[];
let socket,host,browser,c,timer,feeding=false,sample=0;const codes=['FL','FR','FC','LFE','SL','SR'];let muted=-1;
const server=net.createServer(s=>{socket=s;s.on('error',()=>{})});
const check=(v,label)=>{if(!v)throw Error(label);checks.push(label);console.log('PASS '+label)};
function write(p){if(!socket||socket.destroyed)return;const length=Buffer.alloc(4);length.writeInt32LE(p.length);socket.write(Buffer.concat([length,p]))}
function feed(){if(!feeding||!socket)return;const count=960,n=6,names=Buffer.from(codes.join(',')),header=18+names.length;
  write(Buffer.concat([Buffer.from([1]),Buffer.from(JSON.stringify({Version:1,Epoch:41,Name:'Combined view · generated 5.1 PCM',Codec:'PCM fixture',SourceSampleRate:48000,Position:sample/48000,Duration:1000,Playing:true,ClockMilliseconds:Math.floor(os.uptime()*1000),SpeakerStates:codes.map((Code,i)=>({Code,Muted:i===muted,Solo:false,Enabled:i!==muted}))}))]));
  const p=Buffer.alloc(header+count*(n*6+4));p[0]=2;p.writeInt32LE(41,1);p.writeDoubleLE(sample/48000,5);p[13]=names.length;names.copy(p,14);p.writeInt32LE(count,14+names.length);
  for(let f=0;f<count;f++){let l=0,r=0;for(let ch=0;ch<n;ch++){const t=(sample+f)/48000,v=ch===3?.15*Math.sin(t*2*Math.PI*60):.15*Math.sin(t*2*Math.PI*(220+ch*110))+.05*Math.sin(t*2*Math.PI*4000),post=ch===muted?0:v*.7;p.writeInt16LE(Math.round(v*32767),header+(f*n+ch)*2);p.writeFloatLE(post,header+count*n*2+(f*n+ch)*4);if(ch%2)r+=post/n;else l+=post/n}p.writeInt16LE(Math.round(l*32767),header+count*n*6+f*4);p.writeInt16LE(Math.round(r*32767),header+count*n*6+f*4+2)}write(p);sample+=count;
}
async function waitFor(expression,label){for(let i=0;i<160;i++){if(await c.evaluate(expression))return;await delay(50)}throw Error('Timeout: '+label)}
async function capture(name){await c.screenshot(path.join(out,name+'.png'));captures.push(name+'.png')}
async function benchmark(label,ms=1600){
  await c.evaluate('window.__samples=[];window.__sampling=true;(function sample(){if(!window.__sampling)return;const a=renderer.aurora?.stats,s=renderer.spark?.stats;__samples.push([a?.updateMs||0,a?.drawSubmitMs||0,s?.updateMs||0,s?.drawCpuMs||0]);requestAnimationFrame(sample)})()');await delay(ms);
  const result=await c.evaluate('(()=>{__sampling=false;const a=__samples,p=i=>{const v=a.map(x=>x[i]).sort((a,b)=>a-b);return v[Math.floor((v.length-1)*.95)]||0};return{samples:a.length,veilUpdateCpuP95Ms:p(0),veilDrawSubmitCpuP95Ms:p(1),sparkUpdateCpuP95Ms:p(2),sparkDrawCpuP95Ms:p(3),diagnostics:lab.diagnostics}})()');performanceResults.push({label,...result});
}
(async()=>{
  await new Promise(r=>server.listen('\\\\.\\pipe\\'+pipe,r));
  host=spawn(exe,['--headless','--live-pipe',pipe],{windowsHide:true,stdio:'ignore'});
  const endpoint=path.join(path.dirname(exe),'cache/endpoint.json');
  for(let i=0;i<120;i++){try{if(JSON.parse(fs.readFileSync(endpoint)).pid===host.pid)break}catch{}await delay(50)}
  browser=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-sandbox','--disable-gpu-sandbox',`--remote-debugging-port=${port}`,`--user-data-dir=${path.resolve('tools/as-prof')}`,'--no-first-run','--no-default-browser-check','--disable-background-networking','--disable-sync','--autoplay-policy=no-user-gesture-required','about:blank'],{windowsHide:true,stdio:'ignore'});
  for(let i=0;i<100;i++){try{await fetch(`http://127.0.0.1:${port}/json/version`);break}catch{}await delay(100)}
  try{
    c=await connect(endpoint,port);check(!(await c.evaluate('lab.state.playing')),'Startup does not start audio');
    check(await c.evaluate("Array.from($('visual-style').options,o=>o.value).join(',')==='particles,spark-field,hybrid,aurora-spark'"),'Fourth mode is present and previous modes remain');
    await c.evaluate("$('volume').value=0;$('volume').oninput();load('/api/demo',true)");
    await c.evaluate("$('visual-style').value='aurora-spark';$('visual-style').onchange();applyAuroraControls({...Aurora.defaults,intensity:1,opacity:.7});renderer.camera={yaw:.35,pitch:.2,distance:9.5,target:[0,1.5,0]};lab.seek(17).then(()=>lab.play())");
    await delay(1900);check(await c.evaluate('renderer.hasVeil&&renderer.hasSpark&&renderer.aurora.stats.drawCalls>0&&renderer.spark.stats.drawCalls===1&&renderer.gl.getError()===0'),'Both real GPU renderers compile and draw from demo FFT');
    check(await c.evaluate('renderer.count===0&&renderer.beamMesh.vertexCount===0'),'Legacy particles and beams stay idle');
    await c.evaluate('lab.pause()');
    const hash='JSON.stringify([Array.from(renderer.aurora.motion.packed),Array.from(renderer.spark.pool.data.subarray(0,renderer.spark.pool.count*SparkField.STRIDE))])';const frozen=await c.evaluate(hash);
    const imageHash='(()=>{renderer.draw(position());const g=renderer.gl,p=new Uint8Array(g.drawingBufferWidth*g.drawingBufferHeight*4);g.readPixels(0,0,g.drawingBufferWidth,g.drawingBufferHeight,g.RGBA,g.UNSIGNED_BYTE,p);let h=0;for(let i=0;i<p.length;i+=28)h=(Math.imul(h,31)+p[i]+p[i+1]*3+p[i+2]*7)|0;return h})()';
    await capture('aurora-spark-original');const original=await c.evaluate(imageHash);
    await c.evaluate("$('aurora-palette').value='spark';$('aurora-palette').onchange()");await delay(80);await capture('aurora-spark-matched');const matched=await c.evaluate(imageHash);
    check(original!==matched,'Actual canvas pixels differ between original and matching hues');
    check(frozen===await c.evaluate(hash),'Color switching preserves all particle positions and band motion');
    check(await c.evaluate('Array.from(renderer.spark.colors).every((v,i)=>v===renderer.aurora.bandColors[i])'),'Veil and sparks use exactly the same per-band base RGB table');
    await delay(200);check(frozen===await c.evaluate(hash),'Pause freezes both effects');
    await c.evaluate("$('aurora-flow').value='down';$('aurora-flow').onchange();lab.play()");await delay(400);await capture('aurora-spark-waterfall');
    check(await c.evaluate('renderer.aurora.motion.flowPhase.some(x=>x>0)'),'Waterfall flow coexists with sparks');
    await c.evaluate("$('aurora-flow').value='up';$('aurora-flow').onchange()");await delay(150);check(await c.evaluate('renderer.auroraSettings.flowDirection==="up"&&renderer.gl.getError()===0'),'Upward flow also renders');
    await benchmark('Combined · Medium');
    // Verify mode/color changes reuse all cached GPU objects.
    await c.evaluate('window.__alloc={buffers:0,programs:0};for(const [name,key] of [["createBuffer","buffers"],["createProgram","programs"]]){const old=renderer.gl[name].bind(renderer.gl);renderer.gl[name]=(...v)=>{__alloc[key]++;return old(...v)}}');
    for(const mode of ['particles','hybrid','spark-field','aurora-spark','hybrid','aurora-spark'])await c.evaluate(`$('visual-style').value='${mode}';$('visual-style').onchange()`);
    await delay(150);check(await c.evaluate('__alloc.buffers===0&&__alloc.programs===0'),'Switching warmed modes creates no extra GPU buffers or programs');
    await c.evaluate("$('aurora-palette').value='spark';$('aurora-palette').onchange();settingsAction(true)");
    await c.evaluate("$('aurora-palette').value='spectrum';$('aurora-palette').onchange();$('visual-style').value='particles';$('visual-style').onchange();settingsAction(false)");
    check(await c.evaluate("renderer.visualStyle==='aurora-spark'&&renderer.auroraSettings.palette==='spark'&&renderer.auroraSettings.flowDirection==='up'"),'Actual Save/Load restores combo, color and direction');
    await c.evaluate("$('visual-style').dispatchEvent(new Event('change',{bubbles:true}))");await delay(100);await c.send('Page.close');c.close();c=await connect(endpoint,port);
    check(await c.evaluate("renderer.visualStyle==='aurora-spark'&&renderer.auroraSettings.palette==='spark'"),'Reopening the page restores the combined view');
    feeding=true;timer=setInterval(feed,20);await waitFor('liveMode&&playing&&renderer.spark.pool.count>0&&renderer.aurora.stats.drawCalls>0','real linked audio');
    check(await c.evaluate('liveState.hasData&&liveState.channels.length===6'),'Both effects follow real six-channel PCM received over the pipe');
    muted=2;await delay(220);check(await c.evaluate('renderer.spark.stats.perChannel[2].active===0&&renderer.aurora.motion.packets[2].every(x=>x===0)'),'Actual player speaker exclusion removes both effects');
    await c.evaluate("$('analysis-mode').value='output';$('analysis-mode').onchange()");await waitFor('renderer.speakers.length===2&&renderer.spark.pool.count>0','final output');
    check(await c.evaluate('renderer.aurora.motion.channels===2&&renderer.spark.speakers.length===2'),'Final output uses two emitters in both renderers');
    await c.evaluate("$('analysis-mode').value='input';$('analysis-mode').onchange()");await waitFor('renderer.speakers.length===6','input');
    check(await c.evaluate('renderer.spark.stats.perChannel[2].active===0&&renderer.aurora.motion.packets[2].every(x=>x===0)'),'Original input still respects speaker exclusion');
    await benchmark('Linked 5.1 · Medium');
    if(process.env.AURORA_SPARK_AUDIO_VERIFY){
      await c.evaluate("$('spark-quality').value='high';$('spark-quality').onchange();$('aurora-quality').value='high';$('aurora-quality').onchange()");
      const audioOut=path.join(out,'audio-under-combo');fs.mkdirSync(audioOut,{recursive:true});const fd=fs.openSync(path.join(audioOut,'verification.txt'),'w');
      const child=spawn(require('./helpers/environment.cjs').dotnet(),[path.resolve(process.env.AURORA_SPARK_AUDIO_VERIFY),'playback-gui-tests',audioOut],{windowsHide:true,stdio:['ignore',fd,fd],env:process.env});
      await benchmark('Linked 5.1 · High',1800);const code=await new Promise((r,j)=>{child.once('error',j);child.once('exit',r)});fs.closeSync(fd);check(code===0,'Native Stereo/HRTF queue tests pass while both High renderers run');
      performanceResults.push({label:'Audio under High combo',results:JSON.parse(fs.readFileSync(path.join(audioOut,'results.json')))});
    }
    check(await c.evaluate('renderer.gl.getError()===0'),'No final GL errors');check(c.events.every(e=>e.method!=='Runtime.exceptionThrown'),'No runtime exceptions after reopen');
    await capture('linked-5.1-matched');
    performanceResults.push(await c.evaluate('({label:"GPU",renderer:(()=>{const g=renderer.gl,e=g.getExtension("WEBGL_debug_renderer_info");return e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER)})()})'));
  }finally{
    feeding=false;if(timer)clearInterval(timer);if(c){await c.evaluate("api('/api/quit')").catch(()=>{});c.close()}if(socket)socket.destroy();server.close();
    try{const v=await fetch(`http://127.0.0.1:${port}/json/version`).then(r=>r.json()),b=new Cdp(v.webSocketDebuggerUrl);await b.send('Browser.close').catch(()=>{});b.close()}catch{}
    fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({checks,captures,performance:performanceResults},null,2));
  }
  console.log(JSON.stringify({checks:checks.length,performance:performanceResults},null,2));
})().catch(e=>{console.error(e);process.exitCode=1});
