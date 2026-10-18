'use strict';
// Instantaneous artistic light events, not sound propagation or room acoustics.
// No PCM, decoder, forecast, predetermined rail, membrane, FBO, or external dependency.
const SparkField = (() => {
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const defaults = Object.freeze({ density: 1, intensity: 1, streak: .3, spread: .65,
    drift: .45, afterglow: 1, speed: 1, streakLength: 1, twinkle: .6, quality: 'medium', preset: 'surround' });
  const presets = Object.freeze({
    calm: { ...defaults, density: .85, intensity: .85, streak: .1, spread: .85, drift: .6, afterglow: 1.4, preset: 'calm' },
    tesla: { ...defaults, density: 1.3, intensity: 1.2, streak: .8, spread: .55, drift: .75, afterglow: .65, preset: 'tesla' },
    surround: { ...defaults }
  });
  const limits = Object.freeze({ low: 3000, medium: 8000, high: 16000 });
  function sanitize(value) {
    const v = value && typeof value === 'object' ? value : {}, result = { ...defaults };
    for (const [key, min, max] of [['density', .1, 2.5], ['intensity', .1, 2.5], ['streak', 0, 1],
      ['spread', 0, 1.5], ['drift', 0, 1.5], ['afterglow', 0, 2], ['speed', .25, 2.5], ['streakLength', .4, 1.8], ['twinkle', 0, 1]])
      if (typeof v[key] === 'number' && Number.isFinite(v[key])) result[key] = clamp(v[key], min, max);
    if (Object.hasOwn(limits, v.quality)) result.quality = v.quality;
    if (['calm', 'tesla', 'surround', 'custom'].includes(v.preset)) result.preset = v.preset;
    return result;
  }
  const power = (normalized, min = -70, max = 0) => normalized > 0
    ? Math.max(0, 10 ** ((min + (max - min) * clamp(normalized, 0, 1)) / 10) - 10 ** (min / 10)) : 0;

  class Features {
    configure(info, settings) {
      this.size = info.channels.length * info.bandCount;
      this.energy = new Float32Array(this.size); this.onset = new Float32Array(this.size);
      this.fast = new Float32Array(this.size); this.slow = new Float32Array(this.size);
      this.cooldown = new Float32Array(this.size); this.active = new Uint8Array(this.size);
      this.minimumDb = settings?.minimumDb ?? -70; this.maximumDb = settings?.maximumDb ?? 0;
    }
    clear() { for (const key of ['energy', 'onset', 'fast', 'slow', 'cooldown', 'active']) this[key]?.fill(0); }
    hide(channel, bands) { for (const key of ['energy', 'onset', 'fast', 'slow', 'cooldown', 'active']) this[key].fill(0, channel * bands, (channel + 1) * bands); }
    update(values, fastValues, dt) {
      const attack = 1 - Math.exp(-dt / .018), release = 1 - Math.exp(-dt / .12);
      for (let k = 0; k < this.size; k++) {
        const e = clamp(Number.isFinite(values?.[k]) ? values[k] : 0, 0, 1);
        const f = clamp(Number.isFinite(fastValues?.[k]) ? fastValues[k] : e, 0, 1);
        this.active[k] = f > .0001 ? 1 : 0; // True zero never acquires a background emission.
        this.energy[k] = e;
        this.fast[k] += (f - this.fast[k]) * attack;
        this.slow[k] += (f - this.slow[k]) * release;
        this.cooldown[k] = Math.max(0, this.cooldown[k] - dt);
        const rise = Math.max(0, this.fast[k] - this.slow[k] - .035);
        this.onset[k] = this.active[k] && this.cooldown[k] <= 0 ? clamp(rise / .3, 0, 1) : 0;
        if (this.onset[k] > .08) this.cooldown[k] = .055;
      }
    }
  }

  const STRIDE = 20, VERTEX = 13, MAX = 16000, CHUNK = 128;
  // x y z | vx vy vz | age life | RGB | size brightness phase | kind channel band frequency drag sparkle
  class Pool {
    constructor(seed = 1) { this.data = new Float32Array(MAX * STRIDE); this.seed = seed >>> 0 || 1; this.count = 0; }
    random() { let x = this.seed; x ^= x << 13; x ^= x >>> 17; x ^= x << 5; this.seed = x >>> 0; return this.seed / 4294967296; }
    clear() { this.count = 0; }
  }

  const anchors = [[0, 1, .14, .045], [.23, 1, .42, .09], [.42, .38, .94, .58],
    [.58, .06, .86, .95], [.76, .12, .42, 1], [1, .7, .3, 1]];
  function bandColor(f, target, offset) {
    let i = 0; while (i < anchors.length - 2 && f > anchors[i + 1][0]) i++;
    const a = anchors[i], b = anchors[i + 1], t = clamp((f - a[0]) / (b[0] - a[0]), 0, 1);
    for (let c = 0; c < 3; c++) target[offset + c] = a[c + 1] + (b[c + 1] - a[c + 1]) * t;
  }

  class Renderer {
    constructor(gl, seed = (Math.random() * 0xffffffff) >>> 0) {
      this.gl = gl; this.options = sanitize(); this.pool = new Pool(seed); this.features = new Features();
      this.random = this.pool.random.bind(this.pool);
      this.vertices = new Float32Array(MAX * VERTEX);
      this.uploadViews = Array.from({ length: MAX / CHUNK + 1 }, (_, i) => this.vertices.subarray(0, Math.min(MAX, i * CHUNK) * VERTEX));
      this.stats = { particles: 0, micro: 0, streaks: 0, motes: 0, drawCalls: 0, buffers: 0,
        programs: 0, capacity: limits.medium, updateMs: 0, drawCpuMs: 0, emitted: 0, dropped: 0,
        memoryBytes: this.pool.data.byteLength + this.vertices.byteLength, perChannel: [] };
      this.attributeState = Array.from({ length: 4 }, () => ({})); this.saved = {};
      this.stateLocations = []; this.disposed = false;
      try { this.initialize(); } catch (e) { this.dispose(); throw e; }
    }
    initialize() {
      const gl = this.gl, shaders = [];
      const vertex = `precision highp float;
        attribute vec3 aPosition; attribute vec3 aVelocity; attribute vec3 aColor; attribute vec4 aShape;
        uniform mat4 uMatrix; uniform float uHeight;
        varying mediump vec4 vColor; varying mediump vec2 vAxis; varying mediump vec2 vKind;
        void main(){
          vec4 p=uMatrix*vec4(aPosition,1.0), next=uMatrix*vec4(aPosition+aVelocity*.025,1.0);
          gl_Position=p;
          vec2 direction=next.xy/max(.1,next.w)-p.xy/max(.1,p.w);
          vAxis=length(direction)>.00001?normalize(direction):vec2(1.,0.);
          float px=aShape.x*(uHeight/1080.)*9./max(3.,p.w);
          gl_PointSize=clamp(px,2.,aShape.z>.5&&aShape.z<1.5?24.:64.);
          vColor=vec4(aColor,aShape.y); vKind=vec2(aShape.z,aShape.w);
        }`;
      const fragment = `precision mediump float;
        varying mediump vec4 vColor; varying mediump vec2 vAxis; varying mediump vec2 vKind;
        void main(){
          vec2 p=(gl_PointCoord-.5)*2.; p.y=-p.y;
          bool streak=vKind.x>.5&&vKind.x<1.5, mote=vKind.x>1.5;
          if(streak) p=vec2(dot(p,vAxis),dot(p,vec2(-vAxis.y,vAxis.x))*3.3);
          float r2=dot(p,p); if(r2>1.) discard;
          float edge=1.-smoothstep(.55,1.,sqrt(r2));
          float core=exp(-r2*(streak?9.:24.));
          float halo=exp(-r2*(mote?3.6:4.7));
          float shape=mote?halo*.16:(core+halo*.18);
          float a=(1.-exp(-vColor.a*shape))*edge;
          vec3 white=mix(vec3(1.,.96,.86),vec3(.86,.95,1.),vKind.y);
          vec3 rgb=mix(vColor.rgb,white,mote?0.:core*.78);
          gl_FragColor=vec4(rgb,a);
        }`;
      const oldProgram = gl.getParameter(gl.CURRENT_PROGRAM), oldBuffer = gl.getParameter(gl.ARRAY_BUFFER_BINDING);
      try {
        for (const [type, code] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
          const shader = gl.createShader(type); if (!shader) throw new Error('Spark shader allocation failed.');
          shaders.push(shader); gl.shaderSource(shader, code); gl.compileShader(shader);
          if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
        }
        this.program = gl.createProgram(); if (!this.program) throw new Error('Spark program allocation failed.');
        for (const shader of shaders) gl.attachShader(this.program, shader);
        for (const [i, name] of ['aPosition', 'aVelocity', 'aColor', 'aShape'].entries()) gl.bindAttribLocation(this.program, i, name);
        gl.linkProgram(this.program);
        if (!gl.getProgramParameter(this.program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(this.program));
        this.matrixLocation = gl.getUniformLocation(this.program, 'uMatrix'); this.heightLocation = gl.getUniformLocation(this.program, 'uHeight');
        this.buffer = gl.createBuffer(); if (!this.buffer) throw new Error('Spark buffer allocation failed.');
        gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer); gl.bufferData(gl.ARRAY_BUFFER, this.vertices, gl.DYNAMIC_DRAW);
        this.stats.buffers = 1; this.stats.programs = 1;
      } finally {
        for (const shader of shaders) gl.deleteShader(shader);
        gl.useProgram(oldProgram); gl.bindBuffer(gl.ARRAY_BUFFER, oldBuffer);
      }
    }
    configure(speakers, info, settings) {
      this.speakers = speakers; this.info = info;
      this.features.configure(info, settings);
      this.budgets = new Float32Array(speakers.length * info.bandCount);
      this.colors = new Float32Array(info.bandCount * 3); this.frequencies = new Float32Array(info.bandCount);
      for (let b = 0; b < info.bandCount; b++) {
        const f = this.frequencies[b] = clamp(Math.log(Math.max(20, info.centers[b]) / 20) / Math.log(1000), 0, 1);
        bandColor(f, this.colors, b * 3);
      }
      this.channelCounts = new Int32Array(speakers.length); this.channelEmitted = new Float64Array(speakers.length);
      this.stats.perChannel = speakers.map(s => ({ code: s.code, active: 0, emitted: 0 }));
      this.clear();
    }
    setOptions(value) {
      const next = sanitize(value), ratio = next.speed / this.options.speed;
      if (ratio !== 1) for (let p = 0; p < this.pool.count; p++) for (let axis = 3; axis < 6; axis++) this.pool.data[p * STRIDE + axis] *= ratio;
      this.options = next; this.stats.capacity = limits[this.options.quality];
      this.reserve = Math.floor(this.stats.capacity * .75 / Math.max(1, this.speakers?.length || 1));
      this.sharedLimit = this.stats.capacity - this.reserve * (this.speakers?.length || 1);
      while (this.pool.count > this.stats.capacity) this.remove(this.pool.count - 1);
      this.updateShared();
    }
    updateShared() {
      this.reserve = Math.floor(this.stats.capacity * .75 / Math.max(1, this.speakers?.length || 1));
      this.sharedLimit = this.stats.capacity - this.reserve * (this.speakers?.length || 1);
      this.shared = 0; if (this.channelCounts) for (let c = 0; c < this.channelCounts.length; c++) this.shared += Math.max(0, this.channelCounts[c] - this.reserve);
    }
    clear() {
      this.pool.clear(); this.features.clear(); this.budgets?.fill(0); this.channelCounts?.fill(0); this.channelEmitted?.fill(0);
      this.time = 0; this.channelCursor = 0; this.shared = 0;
      this.stats.particles = this.stats.micro = this.stats.streaks = this.stats.motes = this.stats.drawCalls = this.stats.emitted = this.stats.dropped = 0;
      this.updateShared(); this.refreshCounts();
    }
    remove(particle) {
      const a = this.pool.data, i = particle * STRIDE, channel = a[i + 15];
      if (this.channelCounts[channel] > this.reserve) this.shared--;
      this.channelCounts[channel]--; this.pool.count--;
      if (particle !== this.pool.count) a.copyWithin(i, this.pool.count * STRIDE, (this.pool.count + 1) * STRIDE);
    }
    hide(channel) {
      for (let p = this.pool.count - 1; p >= 0; p--) if (this.pool.data[p * STRIDE + 15] === channel) this.remove(p);
      this.features.hide(channel, this.info.bandCount);
      this.budgets.fill(0, channel * this.info.bandCount, (channel + 1) * this.info.bandCount); this.refreshCounts();
    }
    spawn(channel, band, energy, onset) {
      if (this.pool.count >= this.stats.capacity || this.channelCounts[channel] >= this.reserve && this.shared >= this.sharedLimit) { this.stats.dropped++; return; }
      const pool = this.pool, a = pool.data, i = pool.count * STRIDE, random = this.random;
      const o = this.options, s = this.speakers[channel], f = this.frequencies[band], lfe = s.code === 'LFE';
      const moteChance = lfe ? 1 : Math.min(.72, .5 * o.afterglow);
      const choice = random(), kind = choice < moteChance ? 2 : random() < o.streak * (.24 + f * .65) ? 1 : 0;
      const phase = random() * Math.PI * 2;
      const cone = o.spread * (.22 + .72 * (1 - f));
      const azimuth = lfe ? random() * Math.PI * 2 : s.azimuth + (random() + random() - 1) * cone;
      const dx = Math.cos(azimuth), dz = Math.sin(azimuth);
      // Local events populate an energized volume anchored to THIS real speaker.
      // The random birth depth is not a stored rail or an acoustic time of flight.
      const depth = lfe ? Math.sqrt(random()) * 2.8 : random() * s.radius * (1.15 + o.spread * .3);
      const lateral = (random() + random() - 1) * (.18 + o.spread * .8) * (lfe ? 1 : Math.min(1, depth / 2));
      const baseHeight = lfe ? .1 : .3 + 2.35 * f + (s.position[1] - 1.16) * .4;
      a[i] = s.position[0] + dx * depth - dz * lateral;
      a[i + 1] = lfe ? .06 + random() * .16 : Math.max(.045, baseHeight + (random() + random() - 1) * (.25 + o.spread * .65));
      a[i + 2] = s.position[2] + dz * depth + dx * lateral;
      const speed = (lfe ? .25 + random() * .5 : (kind === 2 ? .35 + random() * .8 : .9 + random() * 2.1) * (.65 + energy)) * o.speed;
      a[i + 3] = dx * speed; a[i + 4] = (random() - .5) * (lfe ? .06 : .6 + o.spread * .5) * o.speed; a[i + 5] = dz * speed;
      a[i + 6] = 0;
      a[i + 7] = kind === 2 ? (.65 + random() * 1.05) * (.55 + o.afterglow * .65) : kind === 1 ? .08 + random() * .14 : .055 + random() * .105;
      for (let c = 0; c < 3; c++) a[i + 8 + c] = clamp(this.colors[band * 3 + c] + (random() - .5) * .07, .025, 1);
      if (lfe) { a[i + 8] = 1; a[i + 9] = .24 + random() * .15; a[i + 10] = .055; }
      a[i + 11] = kind === 2 ? 25 + random() * 16 : kind === 1 ? 14 + random() * 12 : 12 + random() * 8;
      a[i + 12] = (kind === 2 ? .65 : 1.6 + onset * 1.4) * (.35 + Math.pow(energy, .7) * 1.2);
      a[i + 13] = phase; a[i + 14] = kind; a[i + 15] = channel; a[i + 16] = band; a[i + 17] = f;
      a[i + 18] = kind === 2 ? .45 : 1.15; a[i + 19] = random();
      if (this.channelCounts[channel] >= this.reserve) this.shared++;
      this.channelCounts[channel]++; this.channelEmitted[channel]++; pool.count++; this.stats.emitted++;
    }
    update(values, fastValues, dt, time, scene) {
      if (!(dt > 0) || !this.info || this.disposed) return;
      const before = performance.now(), step = Math.min(dt, .08); this.time = time;
      // Clamp large deltas: stale visual work never queues behind the audio clock.
      const a = this.pool.data, drift = this.options.drift;
      for (let p = this.pool.count - 1; p >= 0; p--) {
        const i = p * STRIDE; a[i + 6] += dt; // Expire against the audio clock, even after a visual stall.
        if (a[i + 6] >= a[i + 7] || scene.channelSuppressed(a[i + 15]) || this.speakers[a[i + 15]].code === 'LFE' && !scene.showSparkLfe) { this.remove(p); continue; }
        const phase = a[i + 13], age = a[i + 6], force = drift * (a[i + 14] === 2 ? .55 : .9);
        const drag = 1 / (1 + a[i + 18] * step);
        // Distributed phases plus position-dependent continuous force; each frame
        // integrates velocity. There is no curve lookup or synchronized oscillation.
        a[i + 3] = (a[i + 3] + Math.sin(a[i + 2] * .7 + age * 1.6 + phase) * force * step) * drag;
        a[i + 4] = (a[i + 4] + Math.sin(a[i] * .8 - age * 1.3 + phase * 1.7) * force * step * .6) * drag;
        a[i + 5] = (a[i + 5] + Math.sin(a[i + 1] * .9 + age * 1.1 + phase * 2.3) * force * step) * drag;
        a[i] += a[i + 3] * step; a[i + 1] = Math.max(.035, a[i + 1] + a[i + 4] * step); a[i + 2] += a[i + 5] * step;
        if (this.speakers[a[i + 15]].code === 'LFE') a[i + 1] = Math.min(.32, a[i + 1]);
      }
      this.features.update(values, fastValues, step);
      const n = this.speakers.length, bands = this.info.bandCount;
      for (let j = 0; j < n; j++) {
        const channel = (j + this.channelCursor) % n;
        if (scene.channelSuppressed(channel) || this.speakers[channel].code === 'LFE' && !scene.showSparkLfe) { this.hide(channel); continue; }
        for (let b = 0; b < bands; b++) {
          const k = channel * bands + b;
          if (!this.features.active[k]) { this.budgets[k] = 0; continue; }
          const e = this.features.energy[k], onset = this.features.onset[k], f = this.frequencies[b];
          const rate = 105 * (.7 + .5 * f) * e ** .65;
          // Onsets are impulses, not one frame of a rate (which would favor 30 Hz).
          // Never replay missed transients after a suspended/overloaded browser frame.
          const burst = dt > .2 ? 0 : 2.5 * onset;
          this.budgets[k] = Math.min(32, this.budgets[k] + (rate * step + burst) * this.options.density);
          while (this.budgets[k] >= 1) { this.spawn(channel, b, e, onset); this.budgets[k]--; }
        }
      }
      this.channelCursor = (this.channelCursor + 1) % Math.max(1, n);
      this.refreshCounts(); this.stats.updateMs = performance.now() - before;
    }
    refreshCounts() {
      this.stats.particles = this.pool.count; this.stats.micro = this.stats.streaks = this.stats.motes = 0;
      for (let p = 0; p < this.pool.count; p++) {
        const kind = this.pool.data[p * STRIDE + 14];
        if (kind === 0) this.stats.micro++; else if (kind === 1) this.stats.streaks++; else this.stats.motes++;
      }
      for (let c = 0; c < (this.stats.perChannel?.length || 0); c++) {
        this.stats.perChannel[c].active = this.channelCounts[c]; this.stats.perChannel[c].emitted = this.channelEmitted[c];
      }
    }
    saveState() {
      const gl = this.gl, state = this.saved;
      state.program = gl.getParameter(gl.CURRENT_PROGRAM); state.buffer = gl.getParameter(gl.ARRAY_BUFFER_BINDING);
      state.blend = gl.isEnabled(gl.BLEND); state.depth = gl.isEnabled(gl.DEPTH_TEST); state.depthWrite = gl.getParameter(gl.DEPTH_WRITEMASK);
      state.src = gl.getParameter(gl.BLEND_SRC_RGB); state.dst = gl.getParameter(gl.BLEND_DST_RGB);
      state.srcAlpha = gl.getParameter(gl.BLEND_SRC_ALPHA); state.dstAlpha = gl.getParameter(gl.BLEND_DST_ALPHA);
      for (let i = 0; i < 4; i++) {
        const a = this.attributeState[i]; a.enabled = !!gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_ENABLED);
        a.buffer = gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING);
        a.size = gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_SIZE); a.type = gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_TYPE);
        a.normalized = gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_NORMALIZED); a.stride = gl.getVertexAttrib(i, gl.VERTEX_ATTRIB_ARRAY_STRIDE);
        a.offset = gl.getVertexAttribOffset(i, gl.VERTEX_ATTRIB_ARRAY_POINTER);
      }
    }
    restoreState() {
      const gl = this.gl, state = this.saved;
      for (let i = 0; i < 4; i++) {
        const a = this.attributeState[i];
        if (a.buffer) { gl.bindBuffer(gl.ARRAY_BUFFER, a.buffer); gl.vertexAttribPointer(i, a.size, a.type, a.normalized, a.stride, a.offset); }
        if (a.enabled) gl.enableVertexAttribArray(i); else gl.disableVertexAttribArray(i);
      }
      gl.useProgram(state.program); gl.bindBuffer(gl.ARRAY_BUFFER, state.buffer);
      if (state.blend) gl.enable(gl.BLEND); else gl.disable(gl.BLEND);
      if (state.depth) gl.enable(gl.DEPTH_TEST); else gl.disable(gl.DEPTH_TEST);
      gl.depthMask(state.depthWrite); gl.blendFuncSeparate(state.src, state.dst, state.srcAlpha, state.dstAlpha);
    }
    draw(matrix, scene) {
      this.stats.drawCalls = 0;
      if (this.disposed || !this.pool.count) return;
      const before = performance.now(), a = this.pool.data, v = this.vertices;
      for (let p = 0; p < this.pool.count; p++) {
        const i = p * STRIDE, j = p * VERTEX, age = a[i + 6], t = age / a[i + 7], kind = a[i + 14];
        for (let c = 0; c < 6; c++) v[j + c] = a[i + c];
        for (let c = 0; c < 3; c++) v[j + 6 + c] = a[i + 8 + c];
        const fade = kind === 2 ? Math.min(1, age / .09) * (1 - t) ** 1.4 : (1 - t) ** .75;
        const twinkle = kind === 2 ? .8 + .2 * Math.sin(age * 5 + a[i + 13]) : .65 + .35 * Math.sin(age * (45 + a[i + 19] * 90) + a[i + 13]) ** 2;
        v[j + 9] = a[i + 11] * (kind === 1 ? this.options.streakLength : 1);
        v[j + 10] = a[i + 12] * fade * (1 - this.options.twinkle + this.options.twinkle * twinkle) * this.options.intensity;
        v[j + 11] = kind; v[j + 12] = a[i + 17];
      }
      const gl = this.gl;
      this.saveState();
      try {
        gl.useProgram(this.program); gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
        for (const [index, count, offset] of [[0, 3, 0], [1, 3, 12], [2, 3, 24], [3, 4, 36]]) {
          gl.enableVertexAttribArray(index); gl.vertexAttribPointer(index, count, gl.FLOAT, false, VERTEX * 4, offset);
        }
        gl.enable(gl.BLEND); gl.disable(gl.DEPTH_TEST); gl.depthMask(false);
        // Straight RGB/alpha, exactly one alpha weighting. Add light, preserve
        // the opaque canvas alpha; no offscreen feedback or whole-frame flash.
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE, gl.ZERO, gl.ONE);
        gl.uniformMatrix4fv(this.matrixLocation, false, matrix); gl.uniform1f(this.heightLocation, scene.canvas.height);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, this.uploadViews[Math.ceil(this.pool.count / CHUNK)]);
        gl.drawArrays(gl.POINTS, 0, this.pool.count); this.stats.drawCalls = 1;
      } finally { this.restoreState(); }
      this.stats.drawCpuMs = performance.now() - before;
    }
    dispose() {
      if (this.disposed) return; this.disposed = true;
      if (this.buffer) this.gl.deleteBuffer(this.buffer);
      if (this.program) this.gl.deleteProgram(this.program);
      this.buffer = this.program = null;
      if (this.stats) this.stats.buffers = this.stats.programs = this.stats.drawCalls = 0;
    }
  }
  return { defaults, presets, limits, sanitize, power, bandColor, Features, Pool, Renderer, STRIDE };
})();
if (typeof module !== 'undefined' && module.exports) module.exports = SparkField;
