// Saturated-pool benchmark, deliberately synthetic rather than typical music.
const fs=require('node:fs'),path=require('node:path');const {spawn}=require('node:child_process');
const {connect}=require('./helpers/cdp.cjs');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const executable=path.resolve(process.argv[2]||require('./helpers/environment.cjs').viewer());
const output=path.resolve(process.argv[3]||'verification-output/space-sketch/benchmark');
if(executable.includes('playback-fixed-v2'))throw Error('Do not use the protected v2 build.');
fs.mkdirSync(output,{recursive:true});let host,client;const results=[];
async function main(){
  host=spawn(executable,['--headless'],{windowsHide:true,stdio:'ignore'});
  const endpoint=path.join(path.dirname(executable),'cache','endpoint.json');
  for(let i=0;i<100;i++){try{if(JSON.parse(fs.readFileSync(endpoint)).pid===host.pid)break}catch{}await delay(100)}
  client=await connect(endpoint,Number(process.env.SPARK_CDP_PORT||9236));
  await client.evaluate(`$('visual-style').value='spark-field';$('visual-style').onchange();$('volume').value=0;$('volume').oninput();load('/api/demo',true)`);
  await client.evaluate(`window.__stress=true;window.__samples=[];window.__lastFrame=0;window.__sequence=0;
    const update=renderer.spark.update.bind(renderer.spark);renderer.spark.update=function(...args){const t=performance.now();update(...args);
      let tries=0;while(this.pool.count<this.stats.capacity&&tries++<this.stats.capacity*2){const j=window.__sequence++;this.spawn(j%this.speakers.length,Math.floor(j/this.speakers.length)%this.info.bandCount,.85,.4)}
      window.__stressUpdateMs=performance.now()-t;};
    (function sample(now){if(!window.__stress)return;const s=renderer.spark.stats;window.__samples.push([window.__stressUpdateMs||0,s.drawCpuMs,s.particles,window.__lastFrame?now-window.__lastFrame:0]);window.__lastFrame=now;requestAnimationFrame(sample)})(performance.now());
    window.__loop=setInterval(()=>{if(lab.state.time>33)lab.seek(2)},500)`);
  for(const quality of ['low','medium','high']){
    await client.evaluate(`applySparkControls({...SparkField.defaults,quality:'${quality}',afterglow:1.8});renderer.clear();window.__samples=[];window.__lastFrame=0`);
    await delay(3500);
    const data=await client.evaluate(`(()=>{const a=window.__samples.slice(30);const p=(k,q)=>{const b=a.map(v=>v[k]).sort((x,y)=>x-y);return b[Math.floor((b.length-1)*q)]||0};return {quality:'${quality}',samples:a.length,updateCpuP95Ms:p(0,.95),drawCpuP95Ms:p(1,.95),populationP95:p(2,.95),rafP95Ms:p(3,.95),diagnostics:lab.diagnostics,glError:renderer.gl.getError()}})()`);
    if(data.glError||data.populationP95!==({low:3000,medium:8000,high:16000})[quality])throw Error('Pool cap or GL error during '+quality);
    results.push(data);console.log(JSON.stringify(data));
  }
  if(process.env.SPARK_AUDIO_VERIFY){
    const dotnet=process.env.SPARK_DOTNET||require('./helpers/environment.cjs').dotnet();
    const log=fs.openSync(path.join(output,'audio-gui.log'),'w');
    const child=spawn(dotnet,[path.resolve(process.env.SPARK_AUDIO_VERIFY),'playback-gui-tests',path.join(output,'audio-gui')],{windowsHide:true,stdio:['ignore',log,log],env:process.env});
    const code=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',resolve)});
    if(code!==0)throw Error('Native GUI playback failed under saturated GPU load.');
    const audio=JSON.parse(fs.readFileSync(path.join(output,'audio-gui','results.json')));results.push({scenario:'actual Windows playback during 16,000-particle stress',audio});
    console.log('PASS native Stereo and HRTF with the saturated Spark renderer active.');
  }
  fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({kind:'Actual 1080p browser; synthetic saturated pools, not typical music. CPU timing includes submission; GPU execution time not measured.',results,events:client.events},null,2));
}
main().catch(e=>{console.error(e);process.exitCode=1;fs.writeFileSync(path.join(output,'failure.json'),JSON.stringify({error:e.stack,results},null,2));}).finally(async()=>{
  if(client){try{await client.evaluate(`window.__stress=false;clearInterval(window.__loop);lab.pause();api('/api/quit')`)}catch{}client.close()}
  if(host){await delay(700);if(host.exitCode===null)host.kill()}
});
