'use strict';
const $ = id => document.getElementById(id);
const token = location.hash.slice(1);
history.replaceState(null, '', '/');
const api = (path, options = {}) => fetch(path, { method: 'POST', ...options, headers: { 'X-Visualizer-Token': token, ...options.headers } });
api('/api/heartbeat').catch(() => {});
setInterval(() => api('/api/heartbeat').catch(() => {}), 5000);
let clip = null, spectrum = null, audioBuffer = null, context = null, gain = null, source = null;
let playing = false, offset = 0, sourceStart = 0, busy = false, generation = 0;
let sourceEnded = false, lastVisualTime = 0, draggingTimeline = false;
let liveMode = false, liveState = null, liveAt = 0, liveInitialized = false, livePollBusy = false, liveKey = '';

function error(message) { $('notice').textContent = message; $('notice').hidden = false; }
function timeLabel(t) { t = Math.max(0, Math.floor(t)); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`; }
async function ensureAudio() {
  if (!context) {
    context = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
    gain = context.createGain(); gain.gain.value = Number($('volume').value) / 100; gain.connect(context.destination);
  }
  if (context.state === 'suspended') await context.resume();
}
function audibleContextTime() {
  if (!context) return 0;
  const stamp = context.getOutputTimestamp?.();
  // The audio device clock drives both the timeline and the visualization.
  if (stamp?.contextTime > 0 && stamp.performanceTime > 0)
    return Math.min(context.currentTime, stamp.contextTime + Math.max(0, performance.now() - stamp.performanceTime) / 1000);
  return Math.max(0, context.currentTime - (context.outputLatency || context.baseLatency || 0));
}
function position() {
  if (liveMode && liveState) {
    const age = Math.max(0, (performance.now() - liveAt) / 1000);
    return Math.min(liveState.duration, liveState.position + (playing && age < .4 ? age : 0));
  }
  return clip ? Math.min(clip.duration, playing ? offset + Math.max(0, audibleContextTime() - sourceStart) : offset) : 0;
}
function detachSource() {
  if (source) { source.onended = null; try { source.stop(); } catch {} source.disconnect(); source = null; }
}
function pause() {
  offset = position(); playing = false; detachSource(); $('play').textContent = '▶ Play';
  $('state').textContent = clip ? 'Paused' : 'Ready'; lastVisualTime = offset;
}
async function play() {
  if (!clip || busy || liveMode) return;
  await ensureAudio();
  if (offset >= clip.duration - .005) { offset = 0; renderer.clear(); }
  detachSource();
  source = context.createBufferSource(); source.buffer = audioBuffer; source.connect(gain);
  sourceStart = context.currentTime + .05; sourceEnded = false;
  source.onended = () => { sourceEnded = true; };
  source.start(sourceStart, offset); playing = true; lastVisualTime = offset;
  $('play').textContent = 'Ⅱ Pause'; $('state').textContent = 'Playing · Input visualization';
}
function stop() {
  playing = false; detachSource(); offset = 0; lastVisualTime = 0; renderer.clear();
  $('play').textContent = '▶ Play'; $('state').textContent = 'Stopped'; updateTransport();
}
async function seek(seconds) {
  const resume = playing; detachSource(); playing = false;
  offset = Math.max(0, Math.min(clip.duration, seconds)); lastVisualTime = offset; renderer.clear();
  if (resume) await play(); else updateTransport();
}
async function load(path, autoplay = false) {
  if (busy) return;
  if (liveMode) leaveLive();
  const loadGeneration = ++generation;
  pause(); busy = true; $('busy').hidden = false; $('notice').hidden = true;
  $('demo').disabled = $('play').disabled = true;
  try {
    await ensureAudio();
    const response = await api(path);
    if (response.status === 204) return;
    if (!response.ok) { const problem = await response.json(); throw new Error(problem.error || 'Could not load audio.'); }
    const bytes = await response.arrayBuffer();
    if (loadGeneration !== generation) return;
    const headerSize = new DataView(bytes).getInt32(0, true);
    if (headerSize < 2 || headerSize > bytes.byteLength - 4) throw new Error('Invalid audio data header.');
    const metadata = JSON.parse(new TextDecoder().decode(new Uint8Array(bytes, 4, headerSize)));
    const audioOffset = (4 + headerSize + 3) & ~3;
    const expected = audioOffset + metadata.sampleFrames * 8 + metadata.analysisFrames * metadata.channels.length * metadata.bandCount * 4;
    if (expected !== bytes.byteLength) throw new Error('Audio data size mismatch.');
    const samples = new Float32Array(bytes, audioOffset, metadata.sampleFrames * 2);
    const nextAudio = context.createBuffer(2, metadata.sampleFrames, metadata.sampleRate);
    const left = nextAudio.getChannelData(0), right = nextAudio.getChannelData(1);
    for (let i = 0; i < metadata.sampleFrames; i++) { left[i] = samples[i * 2]; right[i] = samples[i * 2 + 1]; }
    // Copy only the small analysis tail; release the interleaved download after loading.
    spectrum = new Float32Array(bytes, audioOffset + metadata.sampleFrames * 8).slice();
    clip = metadata; audioBuffer = nextAudio; offset = 0; lastVisualTime = 0; renderer.setClip(clip);
    $('filename').textContent = clip.name;
    $('format').textContent = `${clip.codec} · ${(clip.sourceSampleRate / 1000).toFixed(1)} kHz · ${clip.bitDepth}${clip.bitDepth === 'Unknown' ? '' : ' bit'} · ${clip.channels.length}ch (${clip.layout})`;
    $('preview-note').textContent = clip.demo ? '40-second demo. Each channel takes a turn.' :
      `Preview: ${timeLabel(clip.duration)}${clip.sourceDuration > clip.duration + .1 ? ' / Full length ' + timeLabel(clip.sourceDuration) : ''} · Analysis and playback at 48 kHz`;
    $('timeline').max = clip.duration; $('timeline').disabled = $('stop').disabled = false;
    $('welcome').hidden = true; $('state').textContent = 'Ready to play'; updateTransport();
  } catch (e) { error(e.message); $('state').textContent = 'Loading error'; }
  finally { busy = false; $('busy').hidden = true; $('demo').disabled = false; $('play').disabled = !clip; }
  if (autoplay && clip && loadGeneration === generation && !$('notice').hidden) return;
  if (autoplay && clip && loadGeneration === generation) await play();
}

const clamp = (x, lo, hi) => Math.max(lo, Math.min(hi, x));
const add = (a, b) => a.map((x, i) => x + b[i]);
const sub = (a, b) => a.map((x, i) => x - b[i]);
const scale = (a, k) => a.map(x => x * k);
const dot = (a, b) => a.reduce((s, x, i) => s + x * b[i], 0);
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm = a => scale(a, 1 / (Math.hypot(...a) || 1));
// CPU particle record: position, velocity, age/life, RGB, size/alpha,
// log-frequency, source channel, band index, fixed horizontal fan sample.
const PARTICLE_STRIDE = 18; // Last value: stable phase for small, smooth XYZ drift.
const frequencyHeight = logFrequency => .18 + clamp(logFrequency, 0, 1) * 2.42;
const LFE_HEIGHT = .12; // An illustrative floor source, not a physical subwoofer position.
function multiply(a, b) {
  const m = new Float32Array(16);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) for (let k = 0; k < 4; k++) m[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k];
  return m;
}
function cameraMatrix(eye, target, aspect, right) {
  // A yaw-derived right vector remains valid when looking exactly up or down.
  const z = norm(sub(eye, target)), x = right, y = cross(z, x);
  const view = new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
  const f = 1 / Math.tan(Math.PI / 7), near = .05, far = 90;
  return multiply(new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0]), view);
}
function color(hz) {
  const h = clamp(Math.log(hz / 20) / Math.log(1000), 0, 1) * .77 * 6;
  const x = 1 - Math.abs(h % 2 - 1), colors = [[1, x, 0], [x, 1, 0], [0, 1, x], [0, x, 1], [x, 0, 1], [1, 0, x]];
  return colors[Math.floor(h) % 6].map(v => .12 + v * .88);
}

class SpaceRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    const gl = this.gl = canvas.getContext('webgl', { alpha: true, antialias: true, premultipliedAlpha: false });
    if (!gl) throw new Error('Cannot start 3D rendering. Check graphics acceleration in Edge.');
    const vertex = `attribute vec3 aPosition; attribute vec4 aColor; attribute float aSize;
      uniform mat4 uMatrix; uniform float uHeight; uniform mediump float uPoint;
      varying mediump vec4 vColor; void main(){gl_Position=uMatrix*vec4(aPosition,1.0);vColor=aColor;
      gl_PointSize=clamp(aSize*uHeight/max(.2,gl_Position.w)*uPoint,1.0,80.0);}`;
    const fragment = `precision mediump float; varying mediump vec4 vColor; uniform mediump float uPoint;
      void main(){float a=vColor.a;if(uPoint>0.5){float r=length(gl_PointCoord-vec2(.5))*2.;if(r>1.)discard;a*=exp(-r*r*4.)*(1.-smoothstep(.65,1.,r));}
      gl_FragColor=vec4(vColor.rgb,a);}`;
    function shader(type, code) { const s = gl.createShader(type); gl.shaderSource(s, code); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; }
    this.program = gl.createProgram(); gl.attachShader(this.program, shader(gl.VERTEX_SHADER, vertex)); gl.attachShader(this.program, shader(gl.FRAGMENT_SHADER, fragment)); gl.linkProgram(this.program);
    if (!gl.getProgramParameter(this.program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(this.program));
    gl.useProgram(this.program);
    this.matrixLocation = gl.getUniformLocation(this.program, 'uMatrix'); this.heightLocation = gl.getUniformLocation(this.program, 'uHeight'); this.pointLocation = gl.getUniformLocation(this.program, 'uPoint');
    this.buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
    for (const [name, count, offset] of [['aPosition', 3, 0], ['aColor', 4, 12], ['aSize', 1, 28]]) {
      const location = gl.getAttribLocation(this.program, name); gl.enableVertexAttribArray(location); gl.vertexAttribPointer(location, count, gl.FLOAT, false, 32, offset);
    }
    gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    this.max = 24000; this.data = new Float32Array(this.max * PARTICLE_STRIDE); this.vertices = new Float32Array(this.max * 8); this.count = 0;
    this.spreadDegrees = 20;
    this.syncReference = 'listener';
    this.particleSpeed = 2;
    this.particleSize = 1; this.particleOpacity = 1;
    this.lines = []; this.speakers = []; this.bands = new Float32Array(0); this.budgets = [];
    this.camera = { yaw: .48, pitch: .56, distance: 12.8, target: [0, .8, 0] }; this.listenerView = false;
    this.setClip({ channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR'], bandCount: 16, centers: Array.from({ length: 16 }, (_, i) => 20 * 1000 ** ((i + .5) / 16)), dispersion: [] });
    this.wireInput();
    canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); pause(); error('3D rendering stopped. Close and reopen the viewer.'); });
  }
  clear() {
    this.count = 0; this.visibleCount = 0; this.budgets.fill(0); this.bands.fill(0);
  }
  setSyncReference(value) {
    const next = value === 'speaker' ? 'speaker' : 'listener';
    if (next !== this.syncReference) { this.syncReference = next; this.clear(); }
  }
  setClip(info) {
    this.info = info; this.clear(); this.budgets = new Array(info.channels.length * info.bandCount).fill(0); this.bands = new Float32Array(this.budgets.length);
    this.frequencies = info.centers.map(hz => clamp(Math.log(hz / 20) / Math.log(1000), 0, 1));
    const angles = { FL: -30, FR: 30, FC: 0, SL: -110, SR: 110, BL: -150, BR: 150, BC: 180, FLC: -15, FRC: 15, TFL: -30, TFR: 30 };
    $('labels').replaceChildren();
    this.speakers = info.channels.map(code => {
      const angle = (angles[code] || 0) * Math.PI / 180;
      const position = code === 'LFE' ? [0, LFE_HEIGHT, 0] : [Math.sin(angle) * 3.5, code.startsWith('T') ? 3 : 1.2, -Math.cos(angle) * 3.5];
      const label = document.createElement('div'); label.className = 'channel-label' + (code === 'LFE' ? ' lfe' : ''); label.textContent = code; $('labels').append(label);
      const direction = norm(sub([0, 1.05, 0], position)), side = norm(cross(direction, [0, 1, 0])), up = norm(cross(side, direction));
      return { code, position, direction, side, up, label, azimuth: Math.atan2(-position[2], -position[0]), radius: Math.hypot(position[0], position[2]) };
    });
    this.buildScene();
  }
  line(a, b, c = [.2, .33, .43], alpha = .45) { this.lines.push(...a, ...c, alpha, 1, ...b, ...c, alpha, 1); }
  circle(center, radius, color, alpha, plane = 'floor', steps = 80) {
    const point = angle => plane === 'floor' ? add(center, [radius * Math.cos(angle), 0, radius * Math.sin(angle)]) : add(center, [radius * Math.cos(angle), radius * Math.sin(angle), 0]);
    for (let i = 0; i < steps; i++) this.line(point(i / steps * Math.PI * 2), point((i + 1) / steps * Math.PI * 2), color, alpha);
  }
  box(center, size, color, alpha) {
    const points = Array.from({ length: 8 }, (_, i) => add(center, size.map((s, k) => s * ((i >> k & 1) ? .5 : -.5))));
    for (let i = 0; i < 8; i++) for (let j = 0; j < 3; j++) if (!(i >> j & 1)) this.line(points[i], points[i | 1 << j], color, alpha);
  }
  buildScene() {
    this.lines = [];
    if ($('grid').checked) {
      for (let i = -6; i <= 6; i++) { this.line([i, 0, -6], [i, 0, 6], [.17, .28, .37], .19); this.line([-6, 0, i], [6, 0, i], [.17, .28, .37], .19); }
      for (let r = 1; r <= 5; r++) this.circle([0, .005, 0], r, [.2, .39, .46], .2);
    }
    for (const s of this.speakers) {
      const col = s.code === 'LFE' ? [.48, .3, .24] : [.32, .49, .56];
      if (s.code === 'LFE') {
        this.circle(s.position, .3, col, .65);
        this.circle(s.position, .12, col, .65);
        continue;
      }
      this.box(s.position, s.code === 'LFE' ? [.5, .55, .5] : [.32, .6, .32], col, .65);
      // Each speaker emits from a frequency column: the same band always has the same height.
      this.line([s.position[0], frequencyHeight(0), s.position[2]], [s.position[0], frequencyHeight(1), s.position[2]], col, .22);
      if (s.code !== 'LFE') this.line([s.position[0], .03, s.position[2]], sub(s.position, [0, .3, 0]), col, .45);
      const center = add(s.position, scale(s.direction, .18));
      for (let i = 0; i < 24; i++) {
        const p = angle => add(center, add(scale(s.side, Math.cos(angle) * .105), scale(s.up, Math.sin(angle) * .105)));
        this.line(p(i * Math.PI / 12), p((i + 1) * Math.PI / 12), col, .75);
      }
    }
    this.box([0, .66, 0], [.37, .57, .23], [.35, .49, .57], .55);
    this.circle([0, 1.16, 0], .19, [.42, .59, .64], .75, 'face', 32);
    this.circle([0, 1.16, 0], .19, [.32, .48, .56], .55, 'floor', 32);
    this.box([0, .36, .08], [.58, .12, .6], [.27, .43, .5], .45);
    this.box([0, .72, .36], [.58, .7, .08], [.27, .43, .5], .45);
    for (const x of [-.24, .24]) for (const z of [-.15, .31]) this.line([x, .3, z], [x, 0, z], [.27, .43, .5], .5);
    this.staticLines = new Float32Array(this.lines);
  }
  wireInput() {
    let drag = null;
    this.canvas.addEventListener('contextmenu', e => e.preventDefault());
    this.canvas.addEventListener('pointerdown', e => { this.canvas.focus(); drag = { x: e.clientX, y: e.clientY, button: e.button }; this.canvas.setPointerCapture(e.pointerId); });
    this.canvas.addEventListener('pointerup', () => { drag = null; });
    this.canvas.addEventListener('pointercancel', () => { drag = null; });
    this.canvas.addEventListener('pointermove', e => {
      if (!drag) return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y; drag.x = e.clientX; drag.y = e.clientY;
      if (drag.button === 2) {
        if (!this.listenerView) { this.camera.target[0] -= dx * .012 * Math.cos(this.camera.yaw); this.camera.target[2] += dx * .012 * Math.sin(this.camera.yaw); this.camera.target[1] += dy * .012; }
      } else this.rotate(-dx * .007, dy * (this.listenerView ? -.007 : .007));
    });
    this.canvas.addEventListener('wheel', e => { e.preventDefault(); if (!this.listenerView) this.camera.distance = clamp(this.camera.distance * Math.exp(e.deltaY * .001), 2, 28); }, { passive: false });
  }
  rotate(yaw, pitch) {
    if (yaw === 0 && pitch === 0) return;
    const turn = Math.PI * 2;
    this.camera.yaw = ((this.camera.yaw + yaw) % turn + turn) % turn;
    this.camera.pitch = clamp(this.camera.pitch + pitch, -Math.PI / 2, Math.PI / 2);
  }
  view(name) {
    this.listenerView = name === 'listener';
    this.camera = { yaw: name === 'front' ? Math.PI : ['back', 'listener', 'top'].includes(name) ? 0 : .48,
      pitch: name === 'listener' ? 0 : name === 'top' ? Math.PI / 2 : name === 'bird' ? Math.PI / 3 : .56,
      distance: ['top', 'bird'].includes(name) ? 13 : 12.8, target: [0, .8, 0] };
  }
  cameraPose() {
    const c = this.camera, z = [Math.sin(c.yaw) * Math.cos(c.pitch), Math.sin(c.pitch) * (this.listenerView ? -1 : 1), Math.cos(c.yaw) * Math.cos(c.pitch)];
    const eye = this.listenerView ? [0, 1.22, .04] : add(c.target, scale(z, c.distance));
    return { eye, target: this.listenerView ? sub(eye, z) : c.target, right: [Math.cos(c.yaw), 0, -Math.sin(c.yaw)] };
  }
  emit(channel, band, intensity, fan = Math.random() * 2 - 1) {
    if (this.count >= this.max) return;
    const s = this.speakers[channel], hz = this.info.centers[band], f = clamp(Math.log(hz / 20) / Math.log(1000), 0, 1), col = color(hz);
    // Height, speed and lifetime illustrate frequency; they are not acoustic propagation physics.
    const life = (6.8 - f * 4.6) * Number($('tail').value) * (.8 + Math.random() * .4);
    const particle = this.count++, i = particle * PARTICLE_STRIDE, a = this.data;
    a[i + 6] = 0; a[i + 7] = life; a[i + 8] = col[0]; a[i + 9] = col[1]; a[i + 10] = col[2];
    a[i + 11] = (.15 - f * .105) * (s.code === 'LFE' ? 1.5 : 1); a[i + 12] = .28 + intensity * .38; a[i + 13] = f;
    a[i + 14] = channel; a[i + 15] = band; a[i + 16] = fan; a[i + 17] = Math.random() * Math.PI * 2;
    a[i + 7] = Math.max(life, this.travelTime(channel, band, fan) + .5);
    this.aimParticle(particle);
  }
  aimParticle(particle) {
    const a = this.data, i = particle * PARTICLE_STRIDE, speaker = this.speakers[a[i + 14]];
    const isLfe = speaker.code === 'LFE';
    const halfAngle = Math.min(this.spreadDegrees, this.info.dispersion[a[i + 15]] ?? 180) * Math.PI / 180;
    // LFE radiates uniformly around the listener's feet, independent of the spread control.
    const angle = isLfe ? a[i + 16] * Math.PI : speaker.azimuth + a[i + 16] * halfAngle;
    const speed = (.95 + a[i + 13] * 2.3) * this.particleSpeed;
    a[i + 3] = Math.cos(angle) * speed; a[i + 4] = 0; a[i + 5] = Math.sin(angle) * speed;
    a[i] = speaker.position[0] + a[i + 3] * a[i + 6];
    a[i + 1] = isLfe ? LFE_HEIGHT : frequencyHeight(a[i + 13]);
    a[i + 2] = speaker.position[2] + a[i + 5] * a[i + 6];
    const age = a[i + 6], arrival = -(speaker.position[0] * a[i + 3] + speaker.position[2] * a[i + 5]) / (speed * speed);
    const envelope = Math.min(1, age * 2) * (arrival > .01 ? Math.min(1, Math.abs(age - arrival) * 3) : 1);
    const amplitude = .07 * envelope, seed = a[i + 17]; // 2% of the 3.5 m speaker radius, each axis.
    a[i] += amplitude * Math.sin(age * 1.17 + seed);
    a[i + 1] += amplitude * Math.sin(age * .91 + seed * 1.37);
    a[i + 2] += amplitude * Math.sin(age * 1.31 + seed * 1.73);

    // A small, smooth vibration remains near the listener in Listener mode. It is only
    // a positional decoration: the emission/travel clock and particle velocity stay unchanged.
    const focus = this.listenerFocus(speaker.position[0] + a[i + 3] * age, speaker.position[2] + a[i + 5] * age);
    const vibration = Math.min(.018, .07 - amplitude) * focus * Math.min(1, age * 2);
    if (vibration > 0) {
      a[i] += vibration * Math.sin(age * 7.3 + seed);
      a[i + 1] += vibration * Math.sin(age * 9.1 + seed * 1.37);
      a[i + 2] += vibration * Math.sin(age * 8.3 + seed * 1.73);
    }

  }
  listenerFocus(x, z) {
    if (this.syncReference !== 'listener') return 0;
    // Frequency is encoded by height, so emphasize the listener's horizontal column.
    // Full emphasis within 25 cm, smoothly fading to normal by 90 cm.
    const t = clamp((Math.hypot(x, z) - .25) / .65, 0, 1);
    return 1 - t * t * (3 - 2 * t);
  }
  travelTime(channel, band, fan) {
    const speaker = this.speakers[channel];
    if (speaker.code === 'LFE') return 0;
    const frequency = this.frequencies[band];
    const angle = fan * Math.min(this.spreadDegrees, this.info.dispersion[band] ?? 180) * Math.PI / 180;
    // The fan need not hit the exact center: synchronize its nearest horizontal approach.
    return speaker.radius * Math.max(0, Math.cos(angle)) / ((.95 + frequency * 2.3) * this.particleSpeed);
  }
  setSpread(degrees) {
    const next = clamp(Number.isFinite(degrees) ? degrees : 20, 0, 90);
    if (next !== this.spreadDegrees && this.syncReference === 'listener') this.clear();
    this.spreadDegrees = next;
    for (let p = 0; p < this.count; p++) this.aimParticle(p);
  }
  setParticleSpeed(value) {
    const next = clamp(Number.isFinite(value) ? value : 2, .5, 4);
    if (next !== this.particleSpeed && this.syncReference === 'listener') this.clear();
    this.particleSpeed = next;
    for (let p = 0; p < this.count; p++) this.aimParticle(p);
  }
  futureBand(time, channel, band) {
    if (!clip || time > clip.duration) return 0;
    const width = this.speakers.length * this.info.bandCount, k = channel * this.info.bandCount + band;
    if (!liveMode) {
      const index = Math.floor((time - clip.firstAnalysisTime) / clip.analysisStep);
      return index < 0 || index >= clip.analysisFrames ? 0 : spectrum[index * width + k];
    }
    const ahead = liveState?.lookahead;
    if (!ahead || ahead.signal !== $('analysis-mode').value || ahead.channels.join(',') !== this.info.channels.join(',')) return 0;
    const times = ahead.times;
    if (!times.length || time < times[0] || time > times[times.length - 1] + .045 || ahead.values.length !== times.length * width) return 0;
    let lo = 0, hi = times.length - 1;
    while (lo < hi) { const mid = Math.ceil((lo + hi) / 2); if (times[mid] <= time) lo = mid; else hi = mid - 1; }
    return ahead.values[lo * width + k];
  }
  advance(dt, time) {
    if (dt <= 0) return;
    const a = this.data;
    for (let p = this.count - 1; p >= 0; p--) {
      const i = p * PARTICLE_STRIDE; a[i + 6] += dt;
      if (a[i + 6] >= a[i + 7]) { this.count--; if (p !== this.count) a.copyWithin(i, this.count * PARTICLE_STRIDE, (this.count + 1) * PARTICLE_STRIDE); continue; }
      this.aimParticle(p); // Age-based drift is bounded and never accumulates.
    }
    if (!clip || !playing) return;
    this.bands.fill(0);
    if (liveMode) {
      const values = liveState?.hasData ? liveState[$('analysis-mode').value] : null;
      if (values?.length === this.bands.length) this.bands.set(values);
    } else {
      let index = Math.floor((time - clip.firstAnalysisTime) / clip.analysisStep);
      if (index < 0 || !clip.analysisFrames) return;
      index = Math.min(clip.analysisFrames - 1, index);
      const size = clip.channels.length * clip.bandCount;
      this.bands.set(spectrum.subarray(index * size, (index + 1) * size));
    }
    const density = Number($('density').value), step = Math.min(dt, .08);
    for (let c = 0; c < this.speakers.length; c++) for (let b = 0; b < clip.bandCount; b++) {
      const k = c * clip.bandCount + b, intensity = this.bands[k];
      if (this.syncReference === 'listener' && this.speakers[c].code !== 'LFE') {
        // Same emitter, geometry and decay as Speaker mode. Only the sampled audio time differs.
        // Candidate thinning gives the same expected emission rate with a per-particle travel offset.
        this.budgets[k] = Math.min(60, this.budgets[k] + step * density * 103);
        while (this.budgets[k] >= 1) {
          const fan = Math.random() * 2 - 1;
          const future = this.futureBand(time + this.travelTime(c, b, fan), c, b);
          if (future > .005 && Math.random() < (3 + 100 * future ** 1.5) / 103) this.emit(c, b, future, fan);
          this.budgets[k]--;
        }
        continue;
      }
      if (intensity <= .005) { this.budgets[k] = 0; continue; }
      this.budgets[k] = Math.min(60, this.budgets[k] + step * density * (3 + 100 * intensity ** 1.5));
      while (this.budgets[k] >= 1) { this.emit(c, b, intensity); this.budgets[k]--; }
    }
  }
  draw(time) {
    const gl = this.gl, canvas = this.canvas;
    const ratio = Math.min(devicePixelRatio || 1, 1.5), width = Math.round(canvas.clientWidth * ratio), height = Math.round(canvas.clientHeight * ratio);
    if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
    gl.viewport(0, 0, width, height); gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    const pose = this.cameraPose();
    this.matrix = cameraMatrix(pose.eye, pose.target, width / Math.max(1, height), pose.right);
    gl.uniformMatrix4fv(this.matrixLocation, false, this.matrix); gl.uniform1f(this.heightLocation, height);
    gl.uniform1f(this.pointLocation, 0); gl.bufferData(gl.ARRAY_BUFFER, this.staticLines, gl.DYNAMIC_DRAW); gl.drawArrays(gl.LINES, 0, this.staticLines.length / 8);
    this.lines = [];
    const lfe = this.speakers.findIndex(s => s.code === 'LFE');
    if ($('lfe').checked && lfe >= 0 && this.bands.length && this.count) {
      let level = 0; for (let b = 0; b < this.info.bandCount; b++) if (this.info.centers[b] <= 160) level = Math.max(level, this.bands[lfe * this.info.bandCount + b]);
      for (let r = 0; r < 4; r++) { const radius = ((time * .8 + r * 1.5) % 6); this.circle([0, .025, 0], radius, [.85, .23, .08], level * .2 * (1 - radius / 7), 'floor', 90); }
      if (this.lines.length) { const rings = new Float32Array(this.lines); gl.bufferData(gl.ARRAY_BUFFER, rings, gl.DYNAMIC_DRAW); gl.drawArrays(gl.LINES, 0, rings.length / 8); }
    }
    this.visibleCount = 0;
    for (let p = 0; p < this.count; p++) {
      const i = p * PARTICLE_STRIDE, v = p * 8, a = this.data, age = a[i + 6] / a[i + 7];
      this.vertices[v] = a[i]; this.vertices[v + 1] = a[i + 1]; this.vertices[v + 2] = a[i + 2];
      this.vertices[v + 3] = a[i + 8]; this.vertices[v + 4] = a[i + 9]; this.vertices[v + 5] = a[i + 10];
      this.vertices[v + 6] = a[i + 12] * Math.min(1, a[i + 6] * 12 + .2) * (1 - age ** 2) * this.particleOpacity;
      // Illustrative frequency-dependent decay beyond the listener, identical in both sync modes.
      // This is not measured air absorption or room reverberation. Bass keeps its long soft tail.
      const speaker = this.speakers[a[i + 14]], speed = (.95 + a[i + 13] * 2.3) * this.particleSpeed;
      const travel = -(speaker.position[0] * a[i + 3] + speaker.position[2] * a[i + 5]) / (speed * speed);
      const pastDistance = Math.max(0, a[i + 6] - travel) * speed;
      this.vertices[v + 6] *= Math.exp(-pastDistance * (.015 + .12 * a[i + 13] ** 1.4));
      this.vertices[v + 7] = a[i + 11] * (1 + age * .4) * this.particleSize;
      const focus = this.listenerFocus(a[i], a[i + 2]);
      this.vertices[v + 6] *= 1 + .5 * focus; // Brighter through opacity; retain each frequency's hue.
      this.vertices[v + 7] *= 1 + .2 * focus;
      if (this.vertices[v + 6] > .001) this.visibleCount++;
    }
    gl.uniform1f(this.pointLocation, 1); gl.bufferData(gl.ARRAY_BUFFER, this.vertices.subarray(0, this.count * 8), gl.DYNAMIC_DRAW); gl.drawArrays(gl.POINTS, 0, this.count);
    for (const s of this.speakers) {
      const p = [...add(s.position, s.code === 'LFE' ? [0, 0, .6] : [0, .48, 0]), 1], q = [0, 0, 0, 0];
      for (let row = 0; row < 4; row++) for (let col = 0; col < 4; col++) q[row] += this.matrix[col * 4 + row] * p[col];
      s.label.hidden = !$('names').checked || q[3] <= 0;
      s.label.style.left = `${(q[0] / q[3] * .5 + .5) * canvas.clientWidth}px`; s.label.style.top = `${(-q[1] / q[3] * .5 + .5) * canvas.clientHeight}px`;
    }
  }
}

let renderer;
try { renderer = new SpaceRenderer($('scene')); }
catch (e) {
  // A graphics failure must not prevent demo audio playback.
  error('Could not start 3D rendering. Demo playback is still available.\n' + e.message);
  renderer = { count: 0, bands: new Float32Array(0), clear() {}, setClip() {}, advance() {}, draw() {}, view() {}, rotate() {}, buildScene() {}, setSpread() {}, setSyncReference() {}, setParticleSpeed() {} };
}

function enterLive() {
  detachSource(); playing = false; sourceEnded = false; liveMode = true; liveKey = '';
  $('welcome').hidden = true; $('notice').hidden = true; $('live-analysis').hidden = false;
  $('play').disabled = $('stop').disabled = $('timeline').disabled = $('volume').disabled = true;
  $('follow').textContent = '● Linked to DiscChannelLab'; $('play').textContent = 'Use player';
  renderer.clear();
  if (liveState) applyLive(liveState);
}
function leaveLive() {
  detachSource(); playing = false; liveMode = false; clip = null; spectrum = null; audioBuffer = null;
  offset = lastVisualTime = 0; renderer.clear(); $('live-analysis').hidden = true;
  $('follow').textContent = 'Link to DiscChannelLab'; $('volume').disabled = false;
  $('play').textContent = '▶ Play'; $('source-mode').textContent = 'Input channels → Stereo playback';
}
function applyLive(state) {
  const mode = $('analysis-mode').value || 'post';
  const channels = mode === 'output' ? ['FL', 'FR'] : state.channels;
  const key = `${mode}:${channels.join(',')}:${state.bandCount}`;
  const previousPosition = position();
  const forecastReset = renderer.syncReference === 'listener' && liveState?.lookahead?.revision !== state.lookahead?.revision;
  const reset = liveState && (state.epoch !== liveState.epoch || state.revision !== liveState.revision);
  liveState = state; liveAt = performance.now();
  if (!liveMode) return;
  if (key !== liveKey) {
    clip = { ...state, channels, layout: channels.join(' / '), demo: false };
    renderer.setClip(clip); liveKey = key; lastVisualTime = state.position;
  } else {
    clip.duration = state.duration;
    if (forecastReset || (reset && (state.playing || Math.abs(state.position - previousPosition) > .2)) || Math.abs(state.position - previousPosition) > .5) renderer.clear();
    if (reset || Math.abs(state.position - previousPosition) > .5) lastVisualTime = state.position;
  }
  playing = state.connected && state.playing;
  $('filename').textContent = state.name;
  $('format').textContent = `${state.codec || 'DiscChannelLab'} · ${(state.sourceSampleRate / 1000).toFixed(1)} kHz · ${state.channels.length ? state.channels.join(' / ') : 'Waiting for audio'}`;
  $('preview-note').textContent = renderer.syncReference === 'listener' && !state.lookahead?.times?.length
    ? 'Preparing look-ahead. If this persists, restart the updated player and playback.'
    : 'Use DiscChannelLab for playback, seeking and volume.';
  $('source-mode').textContent = mode === 'input' ? 'Input · Unaffected by Mute / Solo / Volume' : mode === 'post' ? 'Combined L/R contribution per channel · Includes Mute / Solo / Master' : 'Final stereo PCM · Includes mix, volume and clipping';
  $('timeline').max = Math.max(1, state.duration);
  $('state').textContent = !state.connected ? 'Waiting for player connection' : playing ? (state.hasData ? 'Following DiscChannelLab' : 'Waiting for player audio') : 'Player stopped / paused';
}
async function pollLive() {
  if (livePollBusy || busy) return;
  livePollBusy = true;
  try {
    const response = await api('/api/live?signal=' + encodeURIComponent($('analysis-mode').value || 'post'));
    if (!response.ok || typeof response.json !== 'function') return;
    const state = await response.json();
    if (!state.configured) return;
    $('follow').hidden = false;
    if (!liveInitialized) { liveInitialized = true; liveState = state; enterLive(); }
    else if (liveMode) applyLive(state);
    else liveState = state;
  } catch { /* A lost player/server connection must not interrupt standalone playback. */ }
  finally { livePollBusy = false; }
}
$('follow').onclick = enterLive;
$('analysis-mode').onchange = () => { liveKey = ''; if (liveState) applyLive(liveState); };
$('start-demo').onclick = () => load('/api/demo', true);
$('demo').onclick = () => load('/api/demo', true);
$('play').onclick = () => playing ? pause() : play().catch(e => error(e.message));
$('stop').onclick = stop;
$('volume').oninput = () => { $('volume-value').textContent = `${$('volume').value}%`; if (gain) gain.gain.setTargetAtTime(Number($('volume').value) / 100, context.currentTime, .015); };
$('timeline').oninput = () => { draggingTimeline = true; $('time').textContent = `${timeLabel(Number($('timeline').value))} / ${timeLabel(clip.duration)}`; };
$('timeline').onchange = () => { draggingTimeline = false; seek(Number($('timeline').value)).catch(e => error(e.message)); };
$('view').onchange = () => renderer.view($('view').value);
$('reset-view').onclick = () => { $('view').value = 'orbit'; renderer.view('orbit'); };
$('sync-reference').onchange = () => renderer.setSyncReference($('sync-reference').value);
$('particle-speed').oninput = () => {
  const speed = clamp(Number($('particle-speed').value), .5, 4);
  $('particle-speed-value').textContent = `${speed.toFixed(1)}×`;
  renderer.setParticleSpeed(speed, position());
};
$('grid').onchange = () => renderer.buildScene();
$('density').oninput = () => { $('density-value').textContent = `${Number($('density').value).toFixed(1)}×`; };
$('tail').oninput = () => { $('tail-value').textContent = `${Number($('tail').value).toFixed(1)}×`; };
$('spread').oninput = () => {
  const degrees = clamp(Number($('spread').value), 0, 90);
  $('spread-value').textContent = degrees === 0 ? '0° (direct)' : `±${degrees}°`;
  renderer.setSpread(degrees);
};
$('particle-size').oninput = () => {
  renderer.particleSize = clamp(Number($('particle-size').value), .3, 3);
  $('particle-size-value').textContent = `${renderer.particleSize.toFixed(1)}×`;
};
$('transparency').oninput = () => {
  const percent = clamp(Number($('transparency').value), 0, 100);
  renderer.particleOpacity = 1 - percent / 100;
  $('transparency-value').textContent = `${percent}%`;
};
const cameraKeys = new Set(), arrowKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']);
const editingControl = () => document.activeElement?.isContentEditable || ['INPUT', 'SELECT', 'TEXTAREA', 'BUTTON'].includes(document.activeElement?.tagName);
let uiHidden = false;
function setInterfaceHidden(hidden) {
  uiHidden = Boolean(hidden);
  document.body.classList.toggle('ui-hidden', uiHidden);
  $('toggle-ui').textContent = uiHidden ? 'Show UI · H' : 'Hide UI · H';
  $('toggle-ui').setAttribute('aria-pressed', String(uiHidden));
}
$('toggle-ui').onclick = () => { setInterfaceHidden(!uiHidden); $('scene').focus(); };
document.addEventListener('keydown', e => {
  if (e.code === 'KeyH' && !editingControl() && !e.ctrlKey && !e.altKey && !e.metaKey) {
    e.preventDefault(); if (!e.repeat) setInterfaceHidden(!uiHidden);
  }
  if (e.code === 'KeyS' && (e.ctrlKey || e.metaKey) && !e.altKey) {
    e.preventDefault(); if (!e.repeat && !$('save-settings').disabled) settingsAction(true);
  }
  if (arrowKeys.has(e.code) && !editingControl() && !e.ctrlKey && !e.altKey && !e.metaKey) { e.preventDefault(); cameraKeys.add(e.code); }
  if (!liveMode && e.code === 'Space' && !['INPUT', 'SELECT', 'BUTTON'].includes(document.activeElement.tagName)) { e.preventDefault(); if (clip) playing ? pause() : play(); }
});
document.addEventListener('keyup', e => cameraKeys.delete(e.code));
window.addEventListener('blur', () => cameraKeys.clear());
document.addEventListener('visibilitychange', () => { if (document.hidden) cameraKeys.clear(); });
document.addEventListener('focusin', () => { if (editingControl()) cameraKeys.clear(); });

function captureSettings() {
  return {
    version: 1, signal: $('analysis-mode').value, view: $('view').value, syncReference: $('sync-reference').value,
    spread: Number($('spread').value), particleSize: Number($('particle-size').value), particleSpeed: Number($('particle-speed').value),
    transparency: Number($('transparency').value), density: Number($('density').value),
    persistence: Number($('tail').value), demoVolume: Number($('volume').value),
    grid: $('grid').checked, labels: $('names').checked, lfeRipples: $('lfe').checked, hideUi: uiHidden,
    camera: renderer.camera || { yaw: .48, pitch: .56, distance: 12.8, target: [0, .8, 0] }
  };
}
function applySettings(value) {
  cameraKeys.clear();
  $('sync-reference').value = value.syncReference === 'speaker' ? 'speaker' : 'listener';
  $('sync-reference').onchange();
  $('particle-speed').value = value.particleSpeed ?? 2; $('particle-speed').oninput();
  for (const [id, key] of [['spread', 'spread'], ['particle-size', 'particleSize'], ['transparency', 'transparency'],
    ['density', 'density'], ['tail', 'persistence'], ['volume', 'demoVolume']]) {
    $(id).value = value[key]; $(id).oninput();
  }
  $('grid').checked = value.grid; $('names').checked = value.labels; $('lfe').checked = value.lfeRipples;
  renderer.buildScene();
  $('view').value = value.view; renderer.view(value.view);
  renderer.camera = { ...value.camera, target: [...value.camera.target] };
  $('analysis-mode').value = value.signal; $('analysis-mode').onchange();
  setInterfaceHidden(value.hideUi);
}
async function settingsAction(save, startup = false) {
  $('save-settings').disabled = $('load-settings').disabled = true;
  $('settings-status').textContent = save ? 'Saving…' : 'Loading…';
  try {
    const response = await api(save ? '/api/settings/save' : '/api/settings/load', save ? {
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(captureSettings())
    } : {});
    if (!response.ok) {
      const problem = await response.json();
      throw new Error(problem.error || 'Settings request failed.');
    }
    if (save) $('settings-status').textContent = 'Saved. Restored on next start.';
    else {
      const result = await response.json();
      if (result.settings) applySettings(result.settings);
      $('settings-status').textContent = result.settings ? 'Saved settings restored.' : startup ? 'Use Save settings to remember this view.' : 'No saved settings yet.';
    }
  } catch (e) {
    $('settings-status').textContent = `${save ? 'Save' : 'Load'} failed: ${e.message}`;
  } finally {
    $('save-settings').disabled = $('load-settings').disabled = false;
    $('toggle-ui').title = `H: hide / show UI. Ctrl+S: save settings. ${$('settings-status').textContent}`;
  }
}
$('save-settings').onclick = () => settingsAction(true);
$('load-settings').onclick = () => settingsAction(false);
// Restore preferences before live mode chooses its signal. Never auto-start demo audio.
const settingsReady = settingsAction(false, true);
settingsReady.then(() => { pollLive(); setInterval(pollLive, 50); });
function updateTransport() {
  if (draggingTimeline) return;
  const t = position(); $('timeline').value = t; $('time').textContent = `${timeLabel(t)} / ${timeLabel(clip?.duration || 0)}`;
}
let lastStats = performance.now(), lastCameraTime = lastStats, frameCount = 0, fps = 0;
function render(now) {
  // Camera movement uses wall time so it also works while audio is paused.
  const cameraStep = Math.min(.05, Math.max(0, (now - lastCameraTime) / 1000)) * Math.PI / 3;
  lastCameraTime = now;
  if (!editingControl()) renderer.rotate((Number(cameraKeys.has('ArrowRight')) - Number(cameraKeys.has('ArrowLeft'))) * cameraStep * (renderer.listenerView ? -1 : 1),
    (Number(cameraKeys.has('ArrowUp')) - Number(cameraKeys.has('ArrowDown'))) * cameraStep);
  if (liveMode && now - liveAt > 500) { playing = false; $('state').textContent = 'Waiting for player connection'; }
  const t = position();
  if (playing) {
    renderer.advance(Math.max(0, t - lastVisualTime), t); lastVisualTime = t;
    if (!liveMode && sourceEnded && t >= clip.duration - .002) { offset = clip.duration; playing = false; detachSource(); $('play').textContent = '↻ Replay'; $('state').textContent = 'Playback ended'; }
  }
  renderer.draw(t); updateTransport(); frameCount++;
  if (now - lastStats > 750) { fps = Math.round(frameCount * 1000 / (now - lastStats)); $('stats').textContent = `${(renderer.visibleCount ?? renderer.count).toLocaleString()} particles · ${fps} fps`; lastStats = now; frameCount = 0; }
  requestAnimationFrame(render);
}
requestAnimationFrame(render);
// Read-only diagnostics and small controls for local browser integration checks.
window.lab = {
  get state() { return { playing, liveMode, time: position(), particles: renderer.count, fps, channels: clip?.channels, duration: clip?.duration, contextState: context?.state, errors: $('notice').hidden ? null : $('notice').textContent, bands: Array.from(renderer.bands), view: $('view').value }; },
  play, pause, stop, seek, settingsReady
};
