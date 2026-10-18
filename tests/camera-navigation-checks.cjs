// Deterministic keyboard/camera checks; real browser input is covered separately.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
module.exports=async({sandbox,vm,renderer,element,tick,documentEvents,windowEvents,output})=>{
  const checks=[];const check=(ok,label)=>{assert(ok,label);checks.push(label)};
  const vec=x=>Array.from(x),sub=(a,b)=>a.map((v,i)=>v-b[i]),distance=(a,b)=>Math.hypot(...sub(a,b));
  const key=(type,code,flags={})=>{let prevented=false;documentEvents.get(type)({code,...flags,preventDefault(){prevented=true}});return prevented};
  const pose=()=>({eye:vec(renderer.cameraPose().eye),target:vec(renderer.cameraPose().target)});
  const select=view=>{element('view').value=view;renderer.view(view)};
  const reset=()=>{windowEvents.get('blur')();sandbox.document.activeElement={tagName:'CANVAS'};select('back');renderer.camera.pitch=0};
  const move=(code,flags={},seconds=.5)=>{reset();const p=pose();check(key('keydown',code,flags),'Capture '+code);tick(seconds);key('keyup',code);return{before:p,after:pose()}};
  let p=move('KeyW');check(p.after.eye[2]<p.before.eye[2]&&Math.abs(p.after.eye[0]-p.before.eye[0])<1e-8,'W moves along the current viewing direction');
  p=move('KeyS');check(p.after.eye[2]>p.before.eye[2],'S moves backward');
  p=move('KeyA');check(p.after.eye[0]<p.before.eye[0],'A moves left');
  p=move('KeyD');check(p.after.eye[0]>p.before.eye[0],'D moves right');
  p=move('KeyZ');check(p.after.eye[1]<p.before.eye[1],'Z moves downward');
  p=move('KeyX');check(p.after.eye[1]>p.before.eye[1],'X moves upward');
  p=move('KeyQ');check(distance(p.before.eye,p.after.eye)<1e-8&&p.after.target[0]<p.after.eye[0],'Q turns left in place');
  p=move('KeyE');check(distance(p.before.eye,p.after.eye)<1e-8&&p.after.target[0]>p.after.eye[0],'E turns right in place');
  const normal=move('KeyW'),fast=move('KeyW',{shiftKey:true}),fine=move('KeyW',{altKey:true}),both=move('KeyW',{altKey:true,shiftKey:true});
  const travel=p=>distance(p.before.eye,p.after.eye);
  check(Math.abs(travel(fast)/travel(normal)-4)<1e-7,'Shift increases translation by 4x');
  check(Math.abs(travel(fine)/travel(normal)-.15)<1e-7,'Alt gives 0.15x precision translation');
  check(Math.abs(travel(both)-travel(fine))<1e-8,'Alt has precision priority when both modifiers are held');
  const yawStep=flags=>{reset();key('keydown','KeyQ',flags);tick(.2);key('keyup','KeyQ');return renderer.camera.yaw};
  const turn=yawStep({});check(Math.abs(yawStep({shiftKey:true})/turn-4)<1e-7&&Math.abs(yawStep({altKey:true})/turn-.15)<1e-7,'Modifiers also scale turning');
  reset();key('keydown','KeyW');key('keydown','KeyD');const start=pose();tick(.5);key('keyup','KeyW');key('keyup','KeyD');check(Math.abs(distance(start.eye,pose().eye)-travel(normal))<1e-7,'Diagonal movement is not faster');
  for(const view of ['orbit','bird','top','listener','front','back']){renderer.view(view);const before=pose();renderer.enterFreeView();check(distance(before.eye,pose().eye)<1e-7&&distance(sub(before.target,before.eye).map(x=>x/Math.hypot(...sub(before.target,before.eye))),sub(pose().target,pose().eye).map(x=>x/4))<1e-7,'Entering flight preserves '+view+' view')}
  reset();renderer.enterFreeView();renderer.rotate(0,Math.PI/2);const eye=pose().eye;key('keydown','KeyW');tick(.2);key('keyup','KeyW');renderer.draw(sandbox.lab.state.time);check(pose().eye[1]<eye[1]&&renderer.matrix.every(Number.isFinite),'Vertical view movement has a finite camera matrix');
  reset();renderer.enterFreeView();key('keydown','ArrowUp');tick(.15);key('keyup','ArrowUp');check(pose().target[1]>pose().eye[1],'Arrow Up looks upward while flying');
  reset();key('keydown','KeyW');tick(.1);key('keydown','ShiftLeft',{shiftKey:true});const a=pose();tick(.2);key('keyup','ShiftLeft');const b=pose();tick(.2);const c=pose();key('keyup','KeyW');check(Math.abs(distance(a.eye,b.eye)/distance(b.eye,c.eye)-4)<1e-7,'Changing a modifier while holding movement changes speed immediately');
  reset();key('keydown','KeyW');windowEvents.get('blur')();const held=pose();tick(.3);check(distance(held.eye,pose().eye)===0,'Focus loss cancels movement');
  for(const tagName of ['INPUT','TEXTAREA','SELECT','BUTTON']){reset();sandbox.document.activeElement={tagName};check(!key('keydown','KeyW'),tagName+' keeps its normal keyboard input')}
  reset();check(!key('keydown','KeyW',{ctrlKey:true})&&!key('keydown','KeyW',{metaKey:true}),'Browser/system shortcuts do not move the camera');
  reset();key('keydown','KeyW');key('keydown','KeyS',{ctrlKey:true});const saved=pose();tick(.2);check(distance(saved.eye,pose().eye)===0,'Ctrl+S cancels flight and remains a save shortcut');await Promise.resolve();
  reset();key('keydown','KeyW');tick(.2);key('keyup','KeyW');const beforeSave=pose();await element('save-settings').onclick();renderer.view('orbit');await element('load-settings').onclick();check(renderer.freeView&&element('view').value==='free'&&distance(beforeSave.eye,pose().eye)<1e-7,'Free camera position/orientation survives settings save and load');
  const time=sandbox.lab.state.time,particles=renderer.count;key('keydown','KeyD');tick(.2);key('keyup','KeyD');check(sandbox.lab.state.time===time&&renderer.count===particles,'Camera movement while paused leaves audio time and particles unchanged');
  const direction=p=>sub(p.target,p.eye).map(x=>x/distance(p.target,p.eye));
  for(const view of ['orbit','bird','top','listener','front','back']){
    select(view);const expected=pose();renderer.rotate(.8,.2);renderer.camera.target=[3,4,5];renderer.camera.distance=6;
    check(key('keydown','Home'),'Home is captured in '+view);
    check(element('view').value===view&&!renderer.freeView&&distance(pose().eye,expected.eye)<1e-7&&distance(direction(pose()),direction(expected))<1e-7,'Home restores '+view+' position/direction without changing mode');
    key('keydown','KeyW');tick(.2);key('keydown','KeyQ');tick(.1);check(key('keydown','Home'),'Home is captured after flying from '+view);
    const home=pose();tick(.2);
    check(element('view').value==='free'&&renderer.freeView&&renderer.camera.homeView===view&&distance(home.eye,expected.eye)<1e-7&&distance(direction(home),direction(expected))<1e-7&&distance(home.eye,pose().eye)===0,'Free Home returns to '+view+' defaults, keeps Free mode and clears held movement');
    key('keydown','Home');check(distance(home.eye,pose().eye)<1e-7,'Repeated Home is stable after '+view);
  }
  select('listener');key('keydown','KeyW');tick(.1);key('keyup','KeyW');await element('save-settings').onclick();select('orbit');await element('load-settings').onclick();key('keydown','Home');
  check(renderer.freeView&&element('view').value==='free'&&renderer.camera.homeView==='listener'&&distance(pose().eye,[0,1.65,3])<1e-7,'Saved Free camera remembers Listener as its Home origin');
  select('bird');renderer.camera.target=[3,4,5];element('reset-view').onclick();check(element('view').value==='bird'&&!renderer.freeView&&renderer.camera.pitch===Math.PI/3,'Reset camera button also keeps the selected mode');
  check(sandbox.lab.state.time===time&&renderer.count===particles,'Home never changes paused playback time or particles');
  for(const tagName of ['INPUT','TEXTAREA','SELECT','BUTTON']){sandbox.document.activeElement={tagName};check(!key('keydown','Home'),tagName+' retains normal Home key behavior')}
  const out=path.join(output,'camera-navigation');fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,'logic-results.json'),JSON.stringify({checks},null,2));console.log('PASS navigation: '+checks.length+' checks');
};
