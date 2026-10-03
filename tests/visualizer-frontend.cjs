// Logic check with simulated browser/audio APIs. This does NOT validate GPU rendering or real audio latency.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../DiscChannelLab.Visualizer');
const output = path.resolve(__dirname, '../verification-output');
fs.mkdirSync(output, {recursive: true});
const packet = fs.readFileSync(process.argv[2] || path.join(output, 'visualizer-demo.packet'));
const elements = new Map();
const documentEvents = new Map(), windowEvents = new Map();
let now = 0, drawCalls = 0, lastRaf;
let livePayload = { configured: false }, pollLive;
let storedSettings = { version: 1, signal: 'post', view: 'bird', spread: 20, particleSize: 1, transparency: 0,
  density: 1.3, persistence: 1, demoVolume: 25, grid: true, labels: true, lfeRipples: true,
  camera: {yaw: 1.2, pitch: .9, distance: 13, target: [.2, .8, -.5]} };
let failSettings = false;
const simulateFailure = process.env.LAB_SHADER_FAILURE === '1';
function validateSharedPrecision(program) {
  const uniforms = program.shaders.map(shader => {
    const precision = /precision\s+(\w+)\s+float/.exec(shader.code)?.[1] || 'highp';
    return new Map(Array.from(shader.code.matchAll(/uniform\s+(?:(lowp|mediump|highp)\s+)?(float|vec[234])\s+(\w+)/g), m => [m[3], m[1] || precision]));
  });
  for (const [name, precision] of uniforms[0]) if (uniforms[1].has(name)) assert.equal(precision, uniforms[1].get(name), `Shared uniform precision: ${name}`);
  return !simulateFailure;
}
const gl = new Proxy({
  getShaderParameter: () => true, getProgramParameter: validateSharedPrecision, getProgramInfoLog: () => 'Simulated shader failure', getAttribLocation: () => 0,
  createShader: () => ({}), shaderSource: (shader, code) => { shader.code = code; },
  createProgram: () => ({ shaders: [] }), attachShader: (program, shader) => program.shaders.push(shader), createBuffer: () => ({}), getUniformLocation: () => ({}),
  bufferData: (_target, values) => { assert(values.every(Number.isFinite), 'Non-finite GPU vertex data'); },
  uniformMatrix4fv: (_location, _transpose, values) => { assert(values.every(Number.isFinite), 'Non-finite camera matrix'); },
  drawArrays: () => { drawCalls++; }
}, { get: (target, key) => key in target ? target[key] : key.toUpperCase() === key ? 1 : () => {} });
class Element {
  constructor(id) { this.id = id; this.value = ''; this.hidden = false; this.checked = false; this.style = {}; this.textContent = ''; this.children = []; this.clientWidth = 1440; this.clientHeight = 780; this.tagName = 'BODY'; }
  addEventListener(name, handler) { (this.events ??= new Map()).set(name, handler); }
  focus() { sandbox.document.activeElement = this; }
  setAttribute(name, value) { (this.attributes ??= {})[name] = value; }
  getContext() { return gl; }
  append(child) { this.children.push(child); }
  replaceChildren() { this.children = []; }
  setPointerCapture() {}
}
function element(id) { if (!elements.has(id)) elements.set(id, new Element(id)); return elements.get(id); }
for (const id of ['grid', 'names', 'lfe']) element(id).checked = true;
element('density').value = '1.3'; element('tail').value = '1'; element('volume').value = '25'; element('view').value = 'orbit';
element('analysis-mode').value = 'post';
element('spread').value = '20'; element('particle-size').value = '1'; element('transparency').value = '0';
element('notice').hidden = true;
const audioSources = [];
class AudioContext {
  constructor() { this.state = 'running'; this.destination = {}; this.outputLatency = .08; this.baseLatency = .01; }
  get currentTime() { return now; }
  getOutputTimestamp() { return { contextTime: Math.max(0, now - .08), performanceTime: now * 1000 }; }
  createGain() { return { gain: { value: 0, setTargetAtTime() {} }, connect() {} }; }
  createBuffer(_channels, size) { const data = [new Float32Array(size), new Float32Array(size)]; return { getChannelData: c => data[c] }; }
  createBufferSource() { const source = { connect() {}, disconnect() {}, stop() {}, start(time, offset) { this.started = time; this.offset = offset; } }; audioSources.push(source); return source; }
  async resume() { this.state = 'running'; }
}
const sandbox = {
  console, Float32Array, Uint8Array, DataView, TextDecoder, Math, Array, Number, JSON, Promise, Error,
  encodeURIComponent, performance: { now: () => now * 1000 }, AudioContext, devicePixelRatio: 1,
  document: { getElementById: element, createElement: () => new Element(), addEventListener(name, handler) { documentEvents.set(name, handler); }, activeElement: new Element(),
    body: { classList: { values: new Set(), toggle(name, enabled) { if (enabled) this.values.add(name); else this.values.delete(name); } } } },
  addEventListener(name, handler) { windowEvents.set(name, handler); },
  location: { hash: '#test-token' }, history: { replaceState() {} },
  setInterval(callback, interval) { if (interval === 50) pollLive = callback; }, requestAnimationFrame(callback) { lastRaf = callback; },
  fetch: async (url, options) => {
    if (url.startsWith('/api/settings/')) {
      assert.equal(options.headers['X-Visualizer-Token'], 'test-token');
      if (failSettings) return {ok: false, json: async () => ({error: 'Simulated read/write error'})};
      if (url.endsWith('/save')) {
        assert.equal(options.headers['Content-Type'], 'application/json');
        storedSettings = JSON.parse(options.body);
      }
      return {ok: true, json: async () => ({settings: JSON.parse(JSON.stringify(storedSettings))})};
    }
    return { ok: true, status: 200, json: async () => JSON.parse(JSON.stringify(livePayload)), arrayBuffer: async () => packet.buffer.slice(packet.byteOffset, packet.byteOffset + packet.byteLength) };
  }
};
sandbox.window = sandbox;
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(path.join(root, 'Web/app.js'), 'utf8'), sandbox);
function tick(seconds) { for (let i = 0; i < Math.ceil(seconds * 60); i++) { now += 1 / 60; lastRaf(now * 1000); } }

(async () => {
  await sandbox.lab.settingsReady;
  assert.equal(element('view').value, 'bird');
  assert.equal(element('sync-reference').value, 'listener', 'Old settings default to listener synchronization');
  assert.equal(Number(element('listener-size').value), 1.5, 'Old settings default to stronger listener enlargement');
  assert.equal(element('speaker-beams').checked, false, 'Existing settings retain the particle view');
  assert.equal(Number(element('beam-strength').value), .8, 'Old settings default to modest beam strength');
  assert.equal(vm.runInContext('renderer.particleSpeed', sandbox), simulateFailure ? undefined : 2);
  element('sync-reference').value = 'speaker'; element('sync-reference').onchange();
  assert(!sandbox.lab.state.playing, 'Restoring settings never starts audio');
  if (simulateFailure) {
    assert.equal(typeof element('demo').onclick, 'function');
    await element('demo').onclick();
    await sandbox.lab.play(); tick(1);
    assert(sandbox.lab.state.playing && sandbox.lab.state.time > .5);
    console.log('PASS: Demo playback survives a shader initialization failure.');
    return;
  }
  await element('start-demo').onclick();
  assert(sandbox.lab.state.playing);
  tick(8);
  const running = sandbox.lab.state;
  assert(Math.abs(running.time - (now - .08 - audioSources[0].started)) < .0001, 'Visualization clock must compensate output latency');
  assert(running.particles > 500 && running.particles <= 24000);
  assert.deepEqual(Array.from(running.channels), ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR']);
  sandbox.lab.pause();
  const paused = sandbox.lab.state;
  tick(1);
  assert.equal(sandbox.lab.state.time, paused.time);
  assert.equal(sandbox.lab.state.particles, paused.particles);
  const renderer = vm.runInContext('renderer', sandbox);
  assert.equal(renderer.camera.yaw, 1.2, 'Startup restores exact camera, not just preset');
  const stride = vm.runInContext('PARTICLE_STRIDE', sandbox);
  const adjust = (id, value) => { element(id).value = String(value); element(id).oninput(); };
  renderer.view('listener');
  const rearPose = renderer.cameraPose(), sight = rearPose.target.map((v, i) => v - rearPose.eye[i]);
  assert.deepEqual(Array.from(rearPose.eye), [0, 1.65, 3], 'Listener camera sits behind the central particle region');
  const toCenter = -rearPose.eye[2] / sight[2];
  assert(Math.abs(rearPose.eye[1] + sight[1] * toCenter - 1.3) < 1e-8, 'Preset looks toward listener height at the center');
  renderer.draw(paused.time);
  for (const height of [.12, .18, 1.2, 2.6]) {
    const point = [0,height,0,1], q = [0,0,0,0];
    for (let row = 0; row < 4; row++) for (let col = 0; col < 4; col++) q[row] += renderer.matrix[col*4+row]*point[col];
    assert(q[3] > 0 && Math.abs(q[0]/q[3]) < 1 && Math.abs(q[1]/q[3]) < 1, 'Central bass, treble and LFE are in the listener preset frame');
  }
  const key = (type, code) => {
    let prevented = false;
    documentEvents.get(type)({ code, preventDefault() { prevented = true; } });
    return prevented;
  };
  for (const mode of ['bird', 'listener']) {
    renderer.view(mode);
    const origin = Array.from(renderer.cameraPose().eye);
    assert(key('keydown', 'ArrowUp')); tick(2); key('keyup', 'ArrowUp');
    assert.equal(renderer.camera.pitch, Math.PI / 2);
    const topMatrix = Array.from(renderer.matrix);
    // The projection must not collapse at either pole.
    assert(Math.hypot(topMatrix[0], topMatrix[4], topMatrix[8]) > .1);
    assert(key('keydown', 'ArrowDown')); tick(4); key('keyup', 'ArrowDown');
    assert.equal(renderer.camera.pitch, -Math.PI / 2);
    assert(Math.hypot(renderer.matrix[1], renderer.matrix[5], renderer.matrix[9]) > .1);
    if (mode === 'listener') assert.deepEqual(Array.from(renderer.cameraPose().eye), origin, 'Listener eye stays fixed');
    renderer.rotate(0, Math.PI / 2);
    const oldYaw = renderer.camera.yaw;
    key('keydown', 'ArrowRight'); tick(7); key('keyup', 'ArrowRight');
    const expected = ((oldYaw + (mode === 'listener' ? -1 : 1) * 7 * Math.PI / 3) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
    assert(Math.abs(renderer.camera.yaw - expected) < 1e-8, 'Continuous rotation passes through 360 degrees');
  }
  const releasedYaw = renderer.camera.yaw; tick(.1); assert.equal(renderer.camera.yaw, releasedYaw);
  sandbox.document.activeElement = { tagName: 'INPUT' };
  assert(!key('keydown', 'ArrowRight')); tick(.1); assert.equal(renderer.camera.yaw, releasedYaw);
  sandbox.document.activeElement = new Element();
  key('keydown', 'ArrowRight'); windowEvents.get('blur')(); tick(.1); assert.equal(renderer.camera.yaw, releasedYaw);
  element('scene').events.get('pointerdown')({clientX: 10, clientY: 10, button: 0, pointerId: 1});
  element('scene').events.get('pointermove')({clientX: 20, clientY: 10});
  element('scene').events.get('pointerup')();
  assert(renderer.listenerView, 'Dragging preserves Listener view');
  assert.equal(sandbox.lab.state.time, paused.time, 'Camera controls do not move audio time');
  renderer.view('orbit');
  renderer.clear();
  adjust('spread', 0);
  for (let channel = 0; channel < renderer.speakers.length; channel++)
    for (let band = 0; band < renderer.info.bandCount; band++) renderer.emit(channel, band, .7);
  const heights = Array.from({length: renderer.count}, (_, p) => renderer.data[p * stride + 1]);
  for (let p = 0; p < renderer.count; p++) {
    const i = p * stride, a = renderer.data, speaker = renderer.speakers[a[i + 14]].position;
    if (renderer.speakers[a[i + 14]].code === 'LFE') {
      assert.equal(a[i], 0); assert.equal(a[i + 2], 0);
      assert.equal(a[i + 4], 0);
      assert(Math.abs(a[i + 1] - .12) < 1e-6, 'LFE starts at listener feet');
      continue;
    }
    assert(speaker[0] * a[i + 3] + speaker[2] * a[i + 5] < 0, 'Zero spread points inward');
    assert(Math.abs(speaker[0] * a[i + 5] - speaker[2] * a[i + 3]) < 1e-5, 'Zero spread passes through listener horizontally');
    assert.equal(a[i + 4], 0, 'No vertical velocity');
    assert.equal(a[i + 1], heights[a[i + 15]], 'Same frequency has same height across non-LFE channels');
  }
  for (let b = 1; b < renderer.info.bandCount; b++) assert(heights[b] > heights[b - 1]);
  renderer.advance(.25, paused.time);
  const beforeSpread = renderer.data.slice(0, renderer.count * stride);
  adjust('spread', 45);
  assert.equal(renderer.count, heights.length, 'Spread does not clear particles');
  let changedPosition = false;
  for (let p = 0; p < renderer.count; p++) {
    const i = p * stride, a = renderer.data, s = renderer.speakers[a[i + 14]].position;
    if (renderer.speakers[a[i + 14]].code === 'LFE') {
      assert.equal(a[i + 3], beforeSpread[i + 3]); assert.equal(a[i + 5], beforeSpread[i + 5]);
      assert(Math.abs(a[i + 1] - .12) <= .07001, 'LFE stays close to the floor');
      assert(a[i] * a[i + 3] + a[i + 2] * a[i + 5] > 0, 'LFE moves outward from center');
      continue;
    }
    const cosine = -(s[0] * a[i + 3] + s[2] * a[i + 5]) / (Math.hypot(s[0], s[2]) * Math.hypot(a[i + 3], a[i + 5]));
    const angle = Math.acos(Math.max(-1, Math.min(1, cosine))) * 180 / Math.PI;
    assert(angle <= Math.min(45, renderer.info.dispersion[a[i + 15]]) + .001, 'Horizontal spread obeys slider and frequency limits');
    assert(Math.abs(a[i + 1] - heights[p]) <= .07001, 'Frequency height has only bounded 2% drift');
    assert.equal(a[i + 6], beforeSpread[i + 6], 'Spread preserves age while paused');
    changedPosition ||= a[i] !== beforeSpread[i] || a[i + 2] !== beforeSpread[i + 2];
  }
  assert(changedPosition, 'Spread immediately updates existing particles while paused');
  renderer.draw(paused.time);
  const baseAlpha = renderer.vertices[6], baseSize = renderer.vertices[7];
  const unchangedState = renderer.data.slice(0, renderer.count * stride);
  adjust('particle-size', 2); adjust('transparency', 50); tick(.1);
  assert(Math.abs(renderer.vertices[7] - baseSize * 2) < 1e-6, 'Size immediately scales existing particles');
  assert(Math.abs(renderer.vertices[6] - baseAlpha * .5) < 1e-6, 'Transparency immediately scales alpha');
  adjust('transparency', 100); tick(.1);
  for (let p = 0; p < renderer.count; p++) assert.equal(renderer.vertices[p * 8 + 6], 0, '100% transparency hides every particle');
  assert.deepEqual(renderer.data.slice(0, renderer.count * stride), unchangedState, 'Appearance changes preserve particle simulation while paused');
  assert.equal(sandbox.lab.state.time, paused.time, 'Appearance controls do not seek audio');
  adjust('particle-size', 1); adjust('transparency', 0); adjust('spread', 20);
  renderer.draw(paused.time);
  assert.equal(renderer.vertices[6], baseAlpha); assert.equal(renderer.vertices[7], baseSize);
  // Exercise every azimuth deterministically, without relying on random particle coverage.
  const lfeChannel = renderer.speakers.findIndex(s => s.code === 'LFE');
  renderer.clear(); renderer.emit(lfeChannel, 0, .7);
  for (let degrees = -180; degrees < 180; degrees += 15) {
    renderer.data[16] = degrees / 180;
    renderer.data[6] = 1;
    renderer.aimParticle(0);
    const speed = Math.hypot(renderer.data[3], renderer.data[5]);
    assert(Math.abs(renderer.data[3] / speed - Math.cos(degrees * Math.PI / 180)) < 1e-6);
    assert(Math.abs(renderer.data[5] / speed - Math.sin(degrees * Math.PI / 180)) < 1e-6);
    const original = renderer.data.slice(0, stride);
    for (const spread of [0, 20, 90]) {
      adjust('spread', spread);
      assert.deepEqual(renderer.data.slice(0, stride), original, 'LFE remains omnidirectional at every spread setting');
    }
  }
  adjust('spread', 20);
  await sandbox.lab.seek(25);
  assert.equal(sandbox.lab.state.time, 25);
  assert.equal(sandbox.lab.state.particles, 0);
  await sandbox.lab.play(); tick(1);
  assert(sandbox.lab.state.time > 25.8 && sandbox.lab.state.particles > 0);
  for (const view of ['bird', 'top', 'listener', 'front', 'back', 'orbit']) { element('view').value = view; element('view').onchange(); tick(.1); }
  element('density').value = '3'; element('tail').value = '1.8'; tick(6);
  assert(sandbox.lab.state.particles <= 24000);
  sandbox.lab.stop();
  assert.equal(sandbox.lab.state.time, 0); assert.equal(sandbox.lab.state.particles, 0); assert(!sandbox.lab.state.playing);
  await sandbox.lab.seek(39.5); await sandbox.lab.play();
  audioSources.at(-1).onended(); tick(1);
  assert.equal(sandbox.lab.state.time, 40); assert(!sandbox.lab.state.playing);
  assert(drawCalls > 100);
  const beforeLinkSources = audioSources.length;
  livePayload = { configured: true, connected: true, playing: true, hasData: true, revision: 1, epoch: 1,
    position: 5, duration: 60, name: 'Live source', codec: 'PCM', sourceSampleRate: 48000, sampleRate: 48000,
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR'], bandCount: 16,
    centers: Array.from({length: 16}, (_,i) => 20 * 1000 ** ((i + .5) / 16)), dispersion: new Array(16).fill(70),
    input: new Array(96).fill(.7), post: new Array(96).fill(.5), output: new Array(32).fill(.6),
    fastInput: new Array(96).fill(.65), fastPost: new Array(96).fill(.4), fastOutput: new Array(32).fill(.5) };
  await pollLive(); tick(.2);
  assert(sandbox.lab.state.liveMode && sandbox.lab.state.playing && sandbox.lab.state.particles > 0);
  assert(Math.abs(renderer.lightLevels[0]-.4)<.0001, 'Post brightness uses fast levels rather than the long emission envelope');
  assert.equal(audioSources.length, beforeLinkSources, 'Linked mode must not create a second audio player');
  assert(element('play').disabled && element('volume').disabled);
  element('analysis-mode').value = 'output'; element('analysis-mode').onchange(); tick(.1);
  assert.deepEqual(Array.from(sandbox.lab.state.channels), ['FL', 'FR']);
  assert(Math.abs(renderer.lightLevels[0]-.5)<.001, 'Output brightness follows final stereo levels');
  element('analysis-mode').value = 'post'; element('analysis-mode').onchange();
  livePayload = { ...livePayload, epoch: 2, revision: 2, position: 15, post: new Array(96).fill(0), fastPost: new Array(96).fill(0) };
  await pollLive(); tick(.1);
  assert.equal(sandbox.lab.state.particles, 0, 'Muted Post does not create new particles after a seek');
  assert(renderer.lightLevels.every(x=>x===0), 'Muted Post fast brightness is zero after seek');
  element('analysis-mode').value = 'input'; element('analysis-mode').onchange(); tick(.1);
  assert(sandbox.lab.state.particles > 0, 'Input remains visible with muted Post');
  assert(renderer.lightLevels[0]>.6, 'Input brightness retains unmuted source energy');
  livePayload = { ...livePayload, playing: false, position: 15.2 };
  await pollLive(); const pausedLiveParticles = sandbox.lab.state.particles; tick(.2);
  assert.equal(sandbox.lab.state.particles, pausedLiveParticles);
  livePayload = { ...livePayload, epoch: 3, revision: 3, position: 40 };
  await pollLive(); assert.equal(sandbox.lab.state.particles, 0);
  livePayload = { ...livePayload, connected: false };
  await pollLive(); assert(!sandbox.lab.state.playing);
  await element('demo').onclick();
  assert(!sandbox.lab.state.liveMode && !element('volume').disabled);
  sandbox.lab.pause();
  element('view').value = 'listener'; element('view').onchange(); renderer.rotate(.7, .4);
  adjust('spread', 12); adjust('particle-size', 2.3); adjust('transparency', 65);
  element('grid').checked = false; element('grid').onchange(); element('names').checked = false;
  element('toggle-ui').onclick();
  assert(sandbox.document.body.classList.values.has('ui-hidden'));
  assert.equal(element('toggle-ui').textContent, 'Show UI · H');
  await element('save-settings').onclick();
  const saved = JSON.stringify(storedSettings), timeAtSave = sandbox.lab.state.time;
  adjust('spread', 85); adjust('transparency', 0); renderer.view('top');
  element('toggle-ui').onclick();
  await element('load-settings').onclick();
  assert(sandbox.document.body.classList.values.has('ui-hidden'), 'Saved settings restore hidden UI');
  assert.equal(JSON.stringify(vm.runInContext('captureSettings()', sandbox)), saved, 'Saved settings fully round-trip');
  assert(renderer.listenerView); assert.equal(renderer.spreadDegrees, 12);
  assert.equal(renderer.particleSize, 2.3); assert.equal(renderer.particleOpacity, .35);
  assert.equal(sandbox.lab.state.time, timeAtSave, 'Loading settings does not seek audio');
  failSettings = true;
  await element('save-settings').onclick();
  assert(element('settings-status').textContent.startsWith('Save failed:'));
  assert.equal(JSON.stringify(storedSettings), saved, 'Failed save preserves previous data');
  await element('load-settings').onclick();
  assert.equal(JSON.stringify(vm.runInContext('captureSettings()', sandbox)), saved, 'Failed load preserves current view');
  assert(!element('save-settings').disabled && !element('load-settings').disabled);
  failSettings = false;
  assert(key('keydown', 'KeyH'));
  assert(!sandbox.document.body.classList.values.has('ui-hidden'));
  assert.equal(sandbox.lab.state.time, timeAtSave, 'Hiding/showing UI leaves playback position unchanged');
  // Listener uses the SAME emission geometry; only sample time is shifted forward.
  element('sync-reference').value = 'listener'; element('sync-reference').onchange();
  element('analysis-mode').value = 'post'; adjust('spread', 0); adjust('particle-speed', 2);
  adjust('transparency', 0); adjust('density', 1.3);
  const times = Array.from({length: 220}, (_, i) => 99 + i * .04);
  const values = times.flatMap(t => Array.from({length: 96}, () => t >= 102 && t < 102.2 ? 1 : 0));
  livePayload = { ...livePayload, connected: true, playing: true, hasData: true, epoch: 20, revision: 20,
    position: 100, duration: 200, post: new Array(96).fill(0),
    lookahead: {revision: 1, signal: 'post', channels: ['FL','FR','FC','LFE','SL','SR'], times, values} };
  await element('follow').onclick(); await pollLive();
  // Choose a time that lets the bass burst start at its speaker now and reach the listener at 102 s.
  const bassTravel = renderer.travelTime(0, 0, 0);
  livePayload.position = 102.05 - bassTravel; await pollLive();
  tick(.04);
  const bass = Array.from({length: renderer.count}, (_, p) => p).filter(p => renderer.data[p * stride + 14] === 0 && renderer.data[p * stride + 15] === 0);
  assert(bass.length > 0, 'Future bass emits before it becomes audible');
  for (const p of bass) assert(Math.hypot(renderer.data[p * stride], renderer.data[p * stride + 2]) > 3, 'No particle is injected at the listener');
  const p = bass[0], i = p * stride, snapshot = renderer.data.slice(i, i + stride);
  renderer.data[i + 6] = bassTravel; renderer.aimParticle(p);
  assert(Math.hypot(renderer.data[i], renderer.data[i + 2]) <= .026, 'Burst reaches listener with bounded decorative vibration');
  renderer.data.set(snapshot, i);
  for (const speed of [.5, 1, 2, 4]) {
    adjust('particle-speed', speed);
    assert.equal(renderer.count, 0, 'Retiming clears incompatible trails');
    for (const band of [0, 8, 15]) {
      const travel = renderer.travelTime(0, band, 0), emissionTime = 102.05 - travel;
      assert.equal(renderer.futureBand(emissionTime + travel, 0, band), 1, 'All frequencies schedule the same burst at arrival');
      renderer.emit(0, band, 1, 0);
      const index = (renderer.count - 1) * stride;
      assert(Math.hypot(renderer.data[index], renderer.data[index + 2]) > 3.4, 'Every new particle starts at a speaker');
      renderer.data[index + 6] = travel; renderer.aimParticle(renderer.count - 1);
      assert(Math.hypot(renderer.data[index], renderer.data[index + 2]) <= .026, 'Speed and frequency preserve arrival within the small vibration radius');
      const origin = renderer.speakers[0].position;
      assert(Math.hypot(origin[0] + renderer.data[index+3] * travel, origin[2] + renderer.data[index+5] * travel) < 1e-5, 'Nominal trajectory still reaches the listener at the scheduled time');
    }
  }
  adjust('particle-speed', 2);
  assert.equal(renderer.futureBand(150, 0, 0), 0, 'Missing look-ahead is never extrapolated');
  livePayload.lookahead = {...livePayload.lookahead, revision: 2, values: values.map(() => 0)};
  await pollLive(); assert.equal(renderer.count, 0, 'A changed forecast clears old mixer trails'); tick(.05);
  assert.equal(renderer.count, 0, 'Future silence emits no listener particles');
  // Speaker mode still emits NOW, using identical geometry, drift and fading.
  element('sync-reference').value = 'speaker'; element('sync-reference').onchange();
  livePayload.post.fill(.8); livePayload.position += .1; await pollLive(); tick(.05);
  assert(renderer.count > 0);
  renderer.clear(); renderer.emit(0, 0, .8, 0);
  const travel = renderer.travelTime(0, 0, 0), a = renderer.data, seed = a[17];
  let movedX = false, movedY = false, movedZ = false;
  for (let n = 0; n <= 100; n++) {
    a[6] = n * .03; renderer.aimParticle(0);
    const origin = renderer.speakers[0].position, height = .18 + a[13] * 2.42;
    const delta = [a[0] - origin[0] - a[3] * a[6], a[1] - height, a[2] - origin[2] - a[5] * a[6]];
    delta.forEach(v => assert(Math.abs(v) <= .07001, 'XYZ displacement is at most 2% of speaker radius'));
    movedX ||= Math.abs(delta[0]) > .01; movedY ||= Math.abs(delta[1]) > .01; movedZ ||= Math.abs(delta[2]) > .01;
    const same = Array.from(a.slice(0,stride)); renderer.aimParticle(0);
    assert.deepEqual(Array.from(a.slice(0,stride)), same, 'Paused drift is deterministic');
  }
  assert(movedX && movedY && movedZ); assert.equal(a[17], seed);
  a[6] = travel; renderer.aimParticle(0);
  assert(Math.hypot(a[0],a[2]) < 1e-5, 'Drift vanishes at the synchronization point');
  // Normalized decay at a common distance: high frequencies lose more energy.
  const attenuation = frequency => Math.exp(-5 * (.015 + .12 * frequency ** 1.4));
  renderer.clear(); renderer.emit(0, 0, .8, 0); renderer.emit(0, 15, .8, 0);
  const ratios = [];
  for (let p = 0; p < 2; p++) {
    const i = p * stride, speed = Math.hypot(a[i+3], a[i+5]);
    a[i+6] = renderer.travelTime(0, a[i+15], 0) + 5 / speed;
    a[i+7] = 20; renderer.aimParticle(p);
  }
  renderer.draw(sandbox.lab.state.time);
  for (let p = 0; p < 2; p++) {
    const i = p * stride, unfaded = a[i+12] * (1 - (a[i+6] / a[i+7]) ** 2) * renderer.particleOpacity;
    ratios.push(renderer.vertices[p*8+6] / unfaded);
    assert(Math.abs(ratios[p] - attenuation(a[i+13])) < 1e-5);
  }
  assert(ratios[0] > ratios[1], 'Treble fades faster beyond the listener');
  // Compare identical moving particles in both modes, without changing their age or emission.
  renderer.clear(); renderer.emit(0, 8, .8, 0);
  a[17] = .73; a[6] = renderer.travelTime(0, 8, 0); renderer.aimParticle(0);
  const timing = [a[3], a[5], a[6], a[7]], emissionClock = sandbox.lab.state.time;
  renderer.draw(emissionClock);
  const speakerAlpha = renderer.vertices[6], speakerSize = renderer.vertices[7];
  renderer.syncReference = 'listener'; renderer.lightLevels.fill(1); renderer.aimParticle(0); renderer.draw(emissionClock);
  const centerBrightness = renderer.vertices[6] / speakerAlpha;
  assert(centerBrightness > 1.398 && centerBrightness <= 1.4, 'Listener center is about 40% brighter with small vibration');
  assert(Math.abs(renderer.vertices[7] / speakerSize - 1.5) < 1e-5, 'Listener center is 50% larger by default');
  assert.deepEqual([a[3], a[5], a[6], a[7]], timing, 'Highlight and vibration do not change velocity, age or lifetime');
  assert.equal(renderer.count, 1, 'No stationary particles are injected');
  const particleBeforeSize = renderer.data.slice(0, stride);
  for (const multiplier of [1, 1.2, 2.4, 3]) {
    adjust('listener-size', multiplier); renderer.draw(emissionClock);
    assert(Math.abs(renderer.vertices[7] / speakerSize - multiplier) < 1e-5, 'Listener size slider immediately changes central size');
    assert(Math.abs(renderer.vertices[6] / speakerAlpha - centerBrightness) < 1e-5, 'Size slider does not alter brightness');
  }
  assert.deepEqual(renderer.data.slice(0,stride), particleBeforeSize, 'Sizing does not rebuild, move or retime particles');
  adjust('listener-size', 1.5);
  const frequencyColor = Array.from(renderer.vertices.slice(3,6));
  let previousCenter = 0, previousSurrounding = Infinity;
  for (const multiplier of [1, 1.4, 2, 3, 4]) {
    adjust('listener-brightness', multiplier); renderer.draw(emissionClock);
    assert(Math.abs(renderer.vertices[6] - Math.min(1, speakerAlpha * renderer.listenerBrightnessAt(a[0], a[2]))) < 1e-5, 'Brightness slider changes rendered alpha while paused');
    assert(renderer.vertices[6] >= 0 && renderer.vertices[6] <= 1, 'Strong emphasis stays within the valid blending range');
    assert.deepEqual(Array.from(renderer.vertices.slice(3,6)), frequencyColor, 'Strong emphasis retains frequency colors');
    const central = renderer.listenerBrightnessAt(0,0), surrounding = renderer.listenerBrightnessAt(3.5,0);
    assert(central > previousCenter && surrounding < previousSurrounding, 'Increasing brightness brightens center and dims surroundings');
    previousCenter = central; previousSurrounding = surrounding;
    if (multiplier === 1) assert.equal(surrounding, 1, 'At 1x the whole scene uses normal brightness');
    if (multiplier === 4) {
      assert(surrounding > .35 && surrounding < .37, 'Maximum emphasis retains about 36% distant opacity');
      assert.equal(renderer.vertices[6], 1, 'Bright central particle saturates cleanly without exceeding alpha 1');
    }
    assert(Math.abs(renderer.vertices[7] / speakerSize - 1.5) < 1e-5, 'Brightness leaves size unchanged');
    assert.equal(renderer.listenerBrightnessAt(0,0), multiplier);
    // Quarter- and half-radius samples distinguish a parabola from a linear falloff.
    assert(Math.abs(renderer.listenerBrightnessAt(.15,0) - (central * .9375 + surrounding * .0625)) < 1e-12);
    assert(Math.abs(renderer.listenerBrightnessAt(.3,0) - (central * .75 + surrounding * .25)) < 1e-12);
    let previousRadial = central;
    for (const radius of [.15, .3, .45, .6, 1, 3.5]) {
      const value = renderer.listenerBrightnessAt(radius,0);
      assert(value <= previousRadial, 'Brightness decreases with distance');
      if (radius >= .6) assert.equal(value, surrounding, 'Distant brightness stays at its dimmed floor');
      assert.equal(renderer.listenerBrightnessAt(-radius,0), renderer.listenerBrightnessAt(0,radius), 'Brightness is symmetric around listener');
      previousRadial = value;
    }
    renderer.syncReference = 'speaker';
    assert.equal(renderer.listenerBrightnessAt(0,0), 1, 'Speaker sync ignores central brightness');
    renderer.syncReference = 'listener';
  }
  assert.deepEqual(renderer.data.slice(0,stride), particleBeforeSize, 'Brightness does not change particles or timing');
  adjust('listener-brightness', 1.4);
  const approach = [1.8, 1.5, 1, .6, .25, 0].map(r => 1 + .5 * renderer.listenerFocus(r, 0, 1.8));
  assert.equal(approach[0], 1); assert.equal(approach.at(-1), 1.5);
  for (let i = 1; i < approach.length; i++) assert(approach[i] >= approach[i-1], 'Particles grow progressively toward the listener');
  assert(approach[1] > 1, 'Enlargement already starts outside the old 90 cm emphasis radius');

  const center = [a[0], a[1], a[2]], baseHeight = .18 + a[13] * 2.42;
  assert(Math.hypot(a[0], a[1] - baseHeight, a[2]) > .001, 'Vibration remains at the listener instead of locking to the center');
  assert(Math.hypot(a[0], a[2]) < .026);
  renderer.aimParticle(0); assert.deepEqual([a[0], a[1], a[2]], center, 'Paused vibration stays still');
  a[6] += .03; renderer.aimParticle(0);
  const speaker = renderer.speakers[0].position;
  const laterDrift = [a[0] - speaker[0] - a[3]*a[6], a[1] - baseHeight, a[2] - speaker[2] - a[5]*a[6]];
  assert(laterDrift.some((v, axis) => Math.abs(v - (axis === 1 ? center[1] - baseHeight : center[axis])) > .001), 'Local vibration changes smoothly with playback age');
  assert.equal(renderer.listenerFocus(0,0), 1); assert.equal(renderer.listenerFocus(.25,0), 1);
  assert(renderer.listenerFocus(.4,0) > renderer.listenerFocus(.7,0));
  assert.equal(renderer.listenerFocus(.9,0), 0); assert.equal(renderer.listenerFocus(3.5,0), 0);
  a[6] = 0; renderer.aimParticle(0); renderer.draw(emissionClock);
  const farAlpha = renderer.vertices[6], farSize = renderer.vertices[7], farFactor = renderer.listenerBrightnessAt(a[0],a[2]);
  renderer.syncReference = 'speaker'; renderer.draw(emissionClock);
  assert(farAlpha < renderer.vertices[6], 'Listener sync dims far particles relative to speaker sync');
  assert(Math.abs(farAlpha / renderer.vertices[6] - farFactor) < 1e-5, 'Distant alpha includes the contrast setting only in Listener sync');
  assert.equal(renderer.vertices[7], farSize, 'Source particles keep the same size in both modes');
  renderer.syncReference = 'listener'; a[6] = renderer.travelTime(0,8,0); renderer.aimParticle(0);
  adjust('transparency', 100); renderer.draw(emissionClock); assert.equal(renderer.vertices[6], 0, 'Highlight respects full transparency');
  adjust('transparency', 0);
  assert.equal(sandbox.lab.state.time, emissionClock, 'Highlight never seeks or delays audio');
  element('sync-reference').value = 'listener'; element('sync-reference').onchange();
  adjust('particle-speed', 3.2); adjust('listener-size', 2.4); adjust('listener-brightness', 4); await element('save-settings').onclick();
  element('sync-reference').value = 'speaker'; element('sync-reference').onchange(); adjust('particle-speed', 1);
  adjust('listener-brightness', 1);
  await element('load-settings').onclick();
  assert.equal(renderer.listenerBrightness, 4, 'Listener brightness persists with saved settings');
  assert.equal(renderer.syncReference, 'listener'); assert.equal(renderer.particleSpeed, 3.2); assert.equal(renderer.listenerSize, 2.4, 'Listener size persists with saved settings');
  // Beams are an instantaneous spectrum display, independent of particle travel.
  renderer.emit(0, 8, .7, 0); renderer.data[6] = renderer.travelTime(0,8,0); renderer.aimParticle(0);
  const particleSnapshot = renderer.data.slice(0, renderer.count * stride), beforeBeamTime = sandbox.lab.state.time;
  const beamToggle = on => { element('speaker-beams').checked = on; element('speaker-beams').onchange(); };
  const drawBeams = () => { renderer.lightLevels.set(renderer.bands); renderer.draw(beforeBeamTime); };
  renderer.bands.fill(0); renderer.bands[8] = .8;
  drawBeams(); assert.equal(renderer.beamMesh.vertexCount, 0, 'Beams are opt-in');
  beamToggle(true); drawBeams();
  const mesh = renderer.beamMesh, verticesPerBand = 8 * 14 * 6;
  assert.equal(mesh.vertexCount, verticesPerBand, 'A single active band produces a bounded beam mesh');
  assert(mesh.items.every(item => item.speaker.code !== 'LFE'), 'LFE has no directional beam');
  const beam = mesh.items.find(item => item.channel === 0 && item.band === 8);
  const activeGeometry = () => mesh.vertices.slice(0, mesh.vertexCount * 8);
  let sourceVisible = false, listenerVisible = false, peakAlpha = 0;
  for (let i = 0; i < mesh.vertexCount * 8; i += 8) {
    const v = mesh.vertices, along = (v[i] - beam.x) * beam.dx + (v[i+2] - beam.z) * beam.dz;
    assert(along >= -1e-5 && along <= beam.speaker.radius + 1e-5, 'Beam stays between speaker and listener');
    assert(Math.abs(v[i+1] - beam.height) <= .08001, 'Halo stays near its frequency height');
    beam.color.forEach((c, k) => assert(Math.abs(v[i+3+k]-c) < 1e-6, 'Beam retains frequency color'));
    assert(v[i+6] >= 0 && v[i+6] <= 1, 'Beam opacity is safe');
    if (v[i+6] > .001) {
      sourceVisible ||= Math.hypot(v[i]-beam.x, v[i+2]-beam.z) < .001;
      listenerVisible ||= Math.hypot(v[i],v[i+2]) < .001;
    }
    peakAlpha = Math.max(peakAlpha, v[i+6]);
  }
  assert(sourceVisible && listenerVisible, 'Speaker and listener ends light up in the same draw with no waiting');
  const pausedBeam = activeGeometry(); drawBeams();
  assert.deepEqual(activeGeometry(), pausedBeam, 'Beam freezes while audio is paused');
  const reusedBuffer = mesh.vertices;
  adjust('beam-strength', 1.6); drawBeams();
  assert(Math.max(...Array.from(activeGeometry()).filter((_, i) => i % 8 === 6)) > peakAlpha, 'Beam strength increases rendered opacity');
  assert.equal(mesh.vertices, reusedBuffer, 'Beam buffer is reused instead of growing each frame');
  adjust('beam-strength', 0); drawBeams(); assert.equal(mesh.vertexCount, 0);
  adjust('beam-strength', .8); adjust('transparency', 100); drawBeams(); assert.equal(mesh.vertexCount, 0);
  adjust('transparency', 0);
  renderer.bands.fill(0); renderer.bands[3 * renderer.info.bandCount] = .8;
  drawBeams(); assert.equal(mesh.vertexCount, 0, 'LFE alone keeps its floor field without a spotlight');
  renderer.bands.fill(0); drawBeams(); assert.equal(mesh.vertexCount, 0, 'Silent channels produce no beam');
  renderer.bands[8] = .8;
  for (const view of ['orbit','bird','top','listener','front','back']) {
    renderer.view(view); drawBeams();
    assert.equal(mesh.vertexCount, verticesPerBand, 'Beam works from every camera preset');
  }
  beamToggle(false); drawBeams(); assert.equal(mesh.vertexCount, 0);
  assert.deepEqual(renderer.data.slice(0,renderer.count*stride), particleSnapshot, 'Beam controls preserve moving particles and their vibration');
  assert.equal(sandbox.lab.state.time, beforeBeamTime, 'Beams do not move or delay audio');
  beamToggle(true); adjust('beam-strength', 1.6); await element('save-settings').onclick();
  beamToggle(false); adjust('beam-strength', .2); await element('load-settings').onclick();
  assert(renderer.speakerBeams && renderer.beamStrength === 1.6, 'Beam options survive save and load');
  // Existing central particles respond to today's sound, independently of their birth level.
  renderer.clear(); renderer.syncReference='listener'; adjust('listener-brightness',1.4);
  for (const channel of [0,1,0]) {
    renderer.emit(channel,8,.8,0);
    const p=renderer.count-1; renderer.data[p*stride+6]=p===2 ? 0 : renderer.travelTime(channel,8,0); renderer.aimParticle(p);
  }
  const lightParticleState=renderer.data.slice(0,renderer.count*stride), pulseTime=sandbox.lab.state.time;
  renderer.draw(pulseTime); const otherChannelDark=renderer.vertices[14], distantAlpha=renderer.vertices[22];
  const lightTargets=new Float32Array(renderer.lightLevels.length); lightTargets[8]=1;
  renderer.updateLightLevels(lightTargets,.05); renderer.draw(pulseTime);
  const brightAlpha=renderer.vertices[6];
  assert(renderer.lightLevels[8]>.98, 'Brightness rises within 50ms of receiving current levels');
  assert(brightAlpha>otherChannelDark*10, 'Only the active channel brightens at listener');
  assert.equal(renderer.vertices[14],otherChannelDark, 'Another channel stays dark');
  assert.equal(renderer.vertices[22],distantAlpha, 'The distant stream retains its visual trail');
  lightTargets.fill(0); renderer.updateLightLevels(lightTargets,.12); renderer.draw(pulseTime);
  assert(renderer.vertices[6]<brightAlpha*.1, 'Listener particles become dim within 120ms of silence');
  const frozenLevels=renderer.lightLevels.slice(); renderer.updateLightLevels(new Float32Array(frozenLevels.length).fill(1),0);
  assert.deepEqual(renderer.lightLevels,frozenLevels, 'Brightness envelope freezes with paused audio');
  assert.deepEqual(renderer.data.slice(0,renderer.count*stride),lightParticleState, 'Light modulation does not rebuild or alter vibration, paths and ages');
  assert.equal(sandbox.lab.state.time,pulseTime, 'Fast brightness never seeks or delays audio');
  renderer.syncReference='speaker'; renderer.draw(pulseTime); const sourceTimedAlpha=renderer.vertices[6];
  renderer.lightLevels.fill(1); renderer.draw(pulseTime); assert.equal(renderer.vertices[6],sourceTimedAlpha, 'Speaker sync keeps its original particle brightness');
  renderer.syncReference='listener'; adjust('transparency',100); renderer.draw(pulseTime); assert.equal(renderer.vertices[6],0);
  adjust('transparency',0);
  const result = { mode: 'Simulated APIs; GPU/audio-device validation still required', passed: ['Demo packet parsing', 'Explicit channels', 'Audio output clock compensation', 'Particles generated', 'Pause freezes time and particles', 'Seek clears old particles', 'Resume', 'All six camera matrices finite', 'Particle cap', 'Stop reset', 'Natural end'], particlesAt8Seconds: running.particles, drawCalls };
  result.passed.push('Shared shader uniform precision matches', 'Live mode produces no duplicate audio', 'Output mode has two channels', 'Muted Post / visible Input', 'Live pause and seek', 'Disconnected player stops', 'Return to demo mode');
  result.passed.push('Keyboard tilt reaches both poles without collapsed projection', 'Listener camera stays at its rear viewing position', 'Continuous keyboard rotation beyond 360 degrees', 'Keyboard release and focus respect controls', 'Camera movement works with paused audio');
  result.passed.push('Settings restored on startup without autoplay', 'Save/load all controls and exact camera', 'Settings requests retain authentication', 'Settings I/O errors preserve current state');
  result.passed.push('Hide/show UI and saved visibility leave playback unchanged');
  result.passed.push('Legacy settings default to listener sync and 2x speed', 'Particles begin at speakers using future spectra', 'Bass and treble arrive at the source timestamp', 'Speed 0.5x through 4x preserves arrival timing', 'Missing/stale forecast rejected', 'Mixer forecast revisions clear old trails', 'Sync reference and speed settings round-trip', 'Speaker sync keeps source-timed emission');
  result.passed.push('Listener-only brightness and adjustable size emphasis', 'Smooth local falloff without new stationary particles', 'Listener vibration remains bounded and freezes on pause', 'Emphasis preserves timing and transparency');
  result.passed.push('Listener preset looks toward the center from behind', 'Central bass, treble and LFE fit the listener view');
  result.passed.push('Parabolic brightness limited to central 60 cm', 'Brightness slider updates paused particles without size or timing changes', 'Brightness setting persists', 'Higher brightness dims distant particles', 'Strong emphasis preserves hue and valid alpha');
  result.passed.push('Zero-lag beams illuminate both endpoints together', 'Soft beam meshes aim at listener with fixed frequency heights', 'Beam silence, LFE exclusion, transparency and toggles', 'Beam strength and settings persist', 'Beam views are finite and buffers reused', 'Beams preserve particle vibration and playback time');
  result.passed.push('Fast current levels drive listener particles independently of their age', 'Listener light attack under 50ms and fade under 120ms after data reception', 'Current light keeps channel and signal separation', 'Pulse preserves trails, vibration, pause and playback clock');
  result.passed.push('Progressive enlargement begins 1.8 m out', 'Listener size adjusts live without retiming particles', 'Listener size defaults and persistence');
  result.passed.push('Bounded smooth XYZ drift', 'Drift freezes while paused and vanishes at arrival', 'Frequency-dependent post-listener attenuation');
  result.passed.push('Non-LFE channels aim toward listener at zero spread', 'Base frequency heights are shared and increasing', 'Small vertical drift around frequency height', 'Adjustable horizontal angle limits', 'Paused spread updates existing particles', 'Live size and transparency scaling', 'Full transparency hides particles', 'Appearance preserves audio time and particle state', 'LFE radiates outward from listener feet', 'LFE covers all azimuths independently of spread');
  fs.writeFileSync(path.join(output, 'visualizer-frontend-results.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
