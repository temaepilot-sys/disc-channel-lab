// Actual browser Home events against the published viewer.
const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const {Cdp,connect}=require('./helpers/cdp.cjs'),delay=ms=>new Promise(r=>setTimeout(r,ms));
const folder=path.dirname(require('./helpers/environment.cjs').viewer()),out=path.resolve('verification-output/space-sketch/camera-home');fs.mkdirSync(out,{recursive:true});
const port=9280,checks=[];let host,browser,c;
const check=(v,s)=>{if(!v)throw Error(s);checks.push(s);console.log('PASS '+s)};
const distance=(a,b)=>Math.hypot(...a.map((v,i)=>v-b[i]));const direction=p=>{const v=p.target.map((x,i)=>x-p.eye[i]),length=Math.hypot(...v);return v.map(x=>x/length)};
async function key(type,code){await c.send('Input.dispatchKeyEvent',{type,code,key:code.startsWith('Key')?code.slice(3).toLowerCase():code,windowsVirtualKeyCode:code==='Home'?36:code.startsWith('Key')?code.charCodeAt(3):0})}
async function home(){await key('keyDown','Home');await key('keyUp','Home')}
(async()=>{
  host=spawn(path.join(folder,'DiscChannelLab.Visualizer.exe'),['--headless'],{windowsHide:true,stdio:'ignore'});
  const endpoint=path.join(folder,'cache/endpoint.json');for(let i=0;i<120;i++){try{if(JSON.parse(fs.readFileSync(endpoint)).pid===host.pid)break}catch{}await delay(50)}
  browser=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-sandbox','--disable-gpu-sandbox',`--remote-debugging-port=${port}`,`--user-data-dir=${path.resolve('tools/home-prof')}`,'--no-first-run','--no-default-browser-check','--disable-background-networking','--disable-sync','--autoplay-policy=no-user-gesture-required','about:blank'],{windowsHide:true,stdio:'ignore'});
  for(let i=0;i<100;i++){try{await fetch(`http://127.0.0.1:${port}/json/version`);break}catch{}await delay(100)}
  try{
    c=await connect(endpoint,port);await c.evaluate("$('volume').value=0;$('volume').oninput();load('/api/demo',true)");await delay(350);await c.evaluate('lab.pause()');const time=await c.evaluate('lab.state.time');
    for(const view of ['orbit','bird','top','listener','front','back']){
      await c.evaluate(`$('view').value='${view}';$('view').onchange();$('scene').focus()`);const expected=await c.evaluate('lab.cameraPose');
      await c.evaluate('renderer.rotate(.9,.15);renderer.camera.target=[2,3,4];renderer.camera.distance=6');await home();
      const current=await c.evaluate('({pose:lab.cameraPose,view:lab.state.view,free:renderer.freeView})');
      check(current.view===view&&!current.free&&distance(expected.eye,current.pose.eye)<1e-6&&distance(direction(expected),direction(current.pose))<1e-6,'Home restores '+view+' without changing the mode');
      await key('keyDown','KeyW');await delay(150);await key('keyDown','KeyQ');await delay(100);await home();await key('keyUp','KeyW');await key('keyUp','KeyQ');
      const flight=await c.evaluate('({pose:lab.cameraPose,view:lab.state.view,free:renderer.freeView,origin:renderer.camera.homeView})');
      check(flight.view==='free'&&flight.free&&flight.origin===view&&distance(expected.eye,flight.pose.eye)<1e-6&&distance(direction(expected),direction(flight.pose))<1e-6,'Free Home returns to '+view+' origin and keeps Free movement');
      await delay(150);check(distance(flight.pose.eye,(await c.evaluate('lab.cameraPose')).eye)<1e-7,'Home clears held movement from '+view);
    }
    // The original report concerned Listener: capture both sides of that exact reset.
    await c.evaluate("$('visual-style').value='aurora-spark';$('visual-style').onchange();$('view').value='listener';$('view').onchange();lab.seek(18).then(()=>lab.play())");await delay(600);await c.evaluate("lab.pause();renderer.rotate(.75,-.25);$('scene').focus()");
    await c.screenshot(path.join(out,'listener-before-home.png'));await home();await c.screenshot(path.join(out,'listener-after-home.png'));
    check(await c.evaluate("$('view').value==='listener'&&renderer.listenerView&&!renderer.freeView&&renderer.camera.yaw===0"),'Listener Home restores its front-facing default in Aurora + Spark');
    await key('keyDown','KeyW');await delay(100);await key('keyUp','KeyW');await c.evaluate('settingsAction(true)');
    await c.evaluate("$('view').value='bird';$('view').onchange();settingsAction(false).then(()=>$('scene').focus())");await home();
    check(await c.evaluate("$('view').value==='free'&&renderer.camera.homeView==='listener'&&Math.hypot(...lab.cameraPose.eye.map((x,i)=>x-[0,1.65,3][i]))<1e-7"),'Saved Free movement remembers Listener as Home');
    await c.evaluate('setInterfaceHidden(true)');await key('keyDown','KeyD');await delay(100);await key('keyUp','KeyD');await home();
    check(await c.evaluate("$('view').value==='free'&&renderer.camera.homeView==='listener'"),'Hidden UI Home also keeps its mode');await c.evaluate('setInterfaceHidden(false)');
    await c.evaluate("$('spark-density').focus();renderer.camera.target=[3,4,5]");const before=await c.evaluate('JSON.stringify(renderer.camera)');await home();check(before===await c.evaluate('JSON.stringify(renderer.camera)'),'Home in an input does not reset the camera');
    check(await c.evaluate('renderer.gl.getError()===0&&!lab.state.playing'),'Home leaves paused playback and WebGL healthy');
    check(c.events.every(e=>e.method!=='Runtime.exceptionThrown'),'No JavaScript runtime exceptions');
  }finally{
    if(c){await c.evaluate("api('/api/quit')").catch(()=>{});c.close()}
    try{const v=await fetch(`http://127.0.0.1:${port}/json/version`).then(r=>r.json()),b=new Cdp(v.webSocketDebuggerUrl);await b.send('Browser.close').catch(()=>{});b.close()}catch{}
    fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({checks},null,2));
  }
  console.log('PASS actual Home + WebGL: '+checks.length);
})().catch(e=>{console.error(e);process.exitCode=1});
