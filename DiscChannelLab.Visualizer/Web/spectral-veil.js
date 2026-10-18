'use strict';
// Hybrid Spectral Veil. Aurora is the historical settings key, retained for compatibility.
// FFT magnitudes cannot recover acoustic phase: these are deliberately slowed visual vibrations.
const Aurora = (() => {
  const defaults = Object.freeze({ layout: 'curtain', preset: 'calm', intensity: .75, opacity: .55,
    density: .6, speed: .35, detail: .4, persistence: .65, hybridAmount: .25, quality: 'medium', palette: 'spectrum', flowDirection: 'none' });
  const presets = Object.freeze({ calm: { ...defaults },
    spatial: { ...defaults, preset: 'spatial', opacity: .65, speed: .55, detail: .65, persistence: .15 },
    live: { ...defaults, preset: 'live', intensity: 1.15, opacity: .65, density: .8, speed: .75, detail: .8, persistence: .3 } });
  const budgets = { low: [24, 32, 1], medium: [40, 48, 2], high: [64, 64, 2] };
  const palettes = { spectrum: [[1, .24, .075], [.12, .92, .56], [.4, .3, 1]],
    calm: [[.7, .36, .18], [.14, .68, .59], [.32, .42, .83]], warm: [[.95, .22, .08], [.94, .6, .2], [.75, .32, .54]] };
  const bound = (x, lo, hi) => Math.max(lo, Math.min(hi, x));
  const numeric = (x, fallback, lo, hi) => typeof x === 'number' && Number.isFinite(x) ? bound(x, lo, hi) : fallback;
  function sanitize(value) {
    const v = value && typeof value === 'object' ? value : {}, result = { ...defaults };
    for (const key of ['intensity', 'opacity', 'density', 'speed', 'detail', 'persistence', 'hybridAmount'])
      result[key] = numeric(v[key], defaults[key], 0, key === 'intensity' || key === 'speed' ? 2 : 1);
    for (const [key, choices] of Object.entries({ preset: ['calm', 'spatial', 'live', 'custom'], quality: ['low', 'medium', 'high'], palette: ['spectrum', 'calm', 'warm', 'spark'], flowDirection: ['none', 'down', 'up'] }))
      if (choices.includes(v[key])) result[key] = v[key];
    return result;
  }
  const style = value => ['particles', 'hybrid', 'spark-field', 'aurora-spark'].includes(value) ? value : 'particles';
  const hasSpark = value => value === 'spark-field' || value === 'aurora-spark';
  const hasVeil = value => value === 'hybrid' || value === 'aurora-spark';
  function smooth(previous, target, dt, attack, release) {
    if (!Number.isFinite(dt) || dt <= 0) return previous;
    const tau = target > previous ? attack : release;
    return tau > 0 ? previous + (target - previous) * -Math.expm1(-dt / tau) : target;
  }
  class BandMotion {
    constructor(channels, info) {
      this.channels = channels; this.count = Math.min(32, Math.max(1, info.bandCount)); this.sourceCount = info.bandCount;
      const size = channels * this.count;
      for (const key of ['main', 'fast', 'tail', 'phase', 'onset', 'previous', 'vertical', 'depth']) this[key] = new Float32Array(size);
      this.packed = new Float32Array(channels * 128);
      this.flowPhase = new Float32Array(channels);
      this.packets = Array.from({ length: channels }, (_, c) => this.packed.subarray(c * 128, (c + 1) * 128));
      this.rates = Float32Array.from({ length: this.count }, (_, b) => {
        const f = info.centers?.[b] || 20 * 1000 ** (b / Math.max(1, this.count - 1));
        return .7 + 5.3 * bound(Math.log(f / 20) / Math.log(1000), 0, 1);
      });
      this.clear();
    }
    clear() { for (let c = 0; c < this.channels; c++) this.hide(c); }
    hide(channel) {
      const start = channel * this.count, end = start + this.count;
      for (const key of ['main', 'fast', 'tail', 'onset', 'previous', 'vertical', 'depth']) this[key].fill(0, start, end);
      for (let b = 0; b < this.count; b++) this.phase[start + b] = ((channel + 1) * 2.39996 + b * 1.61803) % (2 * Math.PI);
      this.packets[channel].fill(0);
      this.flowPhase[channel] = 0;
    }
    updateChannel(channel, normal, fast, dt, settings) {
      const c = this.count, start = channel * c, source = channel * this.sourceCount, packet = this.packets[channel];
      dt = bound(dt, 0, .08); // No integration burst after a stalled tab.
      let activitySum = 0;
      for (let b = 0; b < c; b++) {
        const k = start + b, raw = numeric(normal?.[source + b], 0, 0, 1), hit = numeric(fast?.[source + b], raw, 0, 1);
        this.main[k] = smooth(this.main[k], raw, dt, .025, .12);
        this.fast[k] = smooth(this.fast[k], hit, dt, .012, .065);
        this.tail[k] = smooth(this.tail[k], hit, dt, .045, .15 + settings.persistence * .65);
        const flux = Math.max(0, hit - this.previous[k]); this.previous[k] = hit;
        this.onset[k] = Math.max(flux * 2.5, this.onset[k] * Math.exp(-dt / .12));
        const activity = this.fast[k];
        activitySum += activity;
        if (activity > .0001 && settings.speed > 0)
          this.phase[k] = (this.phase[k] + dt * Math.PI * 2 * this.rates[b] * settings.speed * (.5 + .5 * activity)) % (Math.PI * 2);
        const amplitude = activity ** 1.5 * (.08 + .52 * settings.detail) * (1 + .65 * this.onset[k]);
        const phase = this.phase[k], moving = settings.speed > 0 && settings.detail > 0;
        this.vertical[k] = smooth(this.vertical[k], moving ? Math.sin(phase) * amplitude : 0, dt, .018, .018);
        this.depth[k] = smooth(this.depth[k], moving ? Math.sin(phase + b * .9) * amplitude * .6 : 0, dt, .025, .025);
        if (Math.max(raw, hit, this.main[k], this.fast[k], this.tail[k]) < .0001)
          this.main[k] = this.fast[k] = this.tail[k] = this.vertical[k] = this.depth[k] = this.onset[k] = 0;
        const o = b * 4;
        packet[o] = Math.max(this.fast[k], this.tail[k] * .45); packet[o + 1] = Math.min(1, this.onset[k]);
        packet[o + 2] = this.vertical[k]; packet[o + 3] = this.depth[k];
      }
      // Visual flow, not sound propagation. Audio time and this speaker's energy
      // drive it; there are no silent, paused or unrelated wall-clock waves.
      const energy = activitySum / c;
      if (settings.flowDirection !== 'none' && energy > .0001 && settings.speed > 0)
        this.flowPhase[channel] = (this.flowPhase[channel] + dt * (.4 + .6 * energy) * settings.speed) % 1;
    }
  }
  const vertex = `precision highp float;
    attribute vec2 aUv;
    uniform mat4 uMatrix;
    uniform mediump vec3 uOrigin, uDirection, uSide;
    uniform mediump vec4 uBands[32];
    uniform mediump vec3 uColors[32];
    uniform mediump float uBandCount, uDensity, uLayer, uSeed, uLfe;
    varying mediump vec2 vUv;
    varying mediump float vLevel, vFlash, vFold;
    varying mediump vec3 vSparkColor;
    void main() {
      float u=aUv.x, v=aUv.y, index=v*(uBandCount-1.);
      int lo=int(floor(index)); int hi=int(min(float(lo)+1.,uBandCount-1.));
      float t=fract(index); t=t*t*(3.-2.*t);
      vec4 band=mix(uBands[lo],uBands[hi],t);
      float energy=band.x;
      // Width folds are static until this particular band drives displacement.
      float ridge=sin(u*(18.+uDensity*22.)+v*2.+uSeed+uLayer);
      float flex=.35+.65*sin(u*3.14159265), dy=band.z*flex, dz=band.w*(.4+.6*ridge);
      vec3 p;
      if(uLfe>.5) {
        float angle=u*6.2831853, radius=.3+v*3.2+band.z*.5;
        p=vec3(cos(angle)*radius,.08+v*.2+abs(dy)*.35+uLayer*.035,sin(angle)*radius);
      } else {
        p=uOrigin*(.61+uLayer*.075)+uSide*((u-.5)*(3.25+uDensity)*(.85+energy*.2));
        p+=uDirection*(sin(u*3.14159265)*.65+ridge*.075+dz);
        p.y=.12+v*3.35+(uOrigin.y-1.16)*.6+dy;
      }
      vUv=aUv; vLevel=energy; vFlash=band.y; vFold=ridge;
      vSparkColor=mix(uColors[lo],uColors[hi],t);
      gl_Position=uMatrix*vec4(p,1.);
    }`;
  const fragment = `precision mediump float;
    uniform mediump vec3 uLow, uMid, uHigh;
    uniform mediump float uOpacity, uIntensity, uDensity, uLayer, uLfe;
    uniform mediump float uFlowPhase, uFlowDirection, uSeed;
    uniform mediump float uMatchSpark;
    varying mediump vec2 vUv;
    varying mediump float vLevel, vFlash, vFold;
    varying mediump vec3 vSparkColor;
    void main() {
      float u=vUv.x,v=vUv.y;
      float edge=smoothstep(0.,.22,u)*(1.-smoothstep(.78,1.,u));
      float vertical=smoothstep(0.,.045,v)*(1.-smoothstep(.955,1.,v));
      float stripe=.46+.54*pow(.5+.5*vFold,2.);
      // Only light accents travel: frequency color and height remain anchored.
      float flow=step(.5,abs(uFlowDirection))*(1.-step(.5,uLfe));
      if(flow>.5) {
        float cascade=.5+.5*sin(v*22.-uFlowDirection*uFlowPhase*6.2831853+uSeed+sin(u*31.+uSeed)*1.7);
        float threads=pow(.5+.5*sin(u*(105.+uDensity*75.)+sin(v*4.+uSeed)*.6),6.);
        stripe*=.65+.95*pow(cascade,5.)+.45*threads*pow(cascade,2.);
      }
      float alpha=clamp(uOpacity*pow(max(0.,vLevel),1.25)*edge*vertical*stripe*(.85+uDensity*.7)/(1.+uLayer*.65),0.,.82);
      if(uIntensity<=0.||alpha<.001) discard;
      vec3 rgb=v<.5?mix(uLow,uMid,v*2.):mix(uMid,uHigh,(v-.5)*2.);
      if(uMatchSpark>.5) rgb=vSparkColor;
      if(uLfe>.5) rgb=uMatchSpark>.5?vec3(1.,.315,.055):uLow;
      rgb*=min(1.5,uIntensity*(.8+.4*vLevel+.3*vFlash));
      gl_FragColor=vec4(rgb,alpha);
    }`;
  class Renderer {
    constructor(gl) {
      this.gl = gl; this.settings = sanitize(); this.items = [];
      this.stats = { vertices: 0, drawCalls: 0, meshBuilds: 0, bandsPerChannel: 0, updateMs: 0, drawSubmitMs: 0 };
      this.program = null; this.mesh = null;
    }
    configure(speakers, info) {
      this.motion = new BandMotion(speakers.length, info); this.stats.bandsPerChannel = this.motion.count;
      // Reuse Spark's exact log-frequency base hues. Its hot white cores and tiny
      // random color variations belong to sparks, not to the translucent veil.
      this.bandColors = new Float32Array(32 * 3);
      for (let b = 0; b < this.motion.count; b++) {
        const hz = info.centers?.[b] || 20 * 1000 ** ((b + .5) / this.motion.count);
        SparkField.bandColor(bound(Math.log(Math.max(20, hz) / 20) / Math.log(1000), 0, 1), this.bandColors, b * 3);
      }
      this.items = speakers.map((speaker, channel) => ({ speaker, channel, seed: (channel + 1) * 2.39996 })); this.clear();
    }
    clear() { this.motion?.clear(); this.stats.drawCalls = 0; this.stats.vertices = 0; }
    hide(channel) { this.motion?.hide(channel); }
    update(normal, fast, dt, time, scene) {
      if (!this.motion || !Number.isFinite(dt) || dt <= 0) return;
      const before = performance.now();
      for (const { speaker, channel } of this.items) {
        if (scene.channelSuppressed(channel) || (speaker.code === 'LFE' && !document.getElementById('lfe').checked)) { this.hide(channel); continue; }
        this.motion.updateChannel(channel, normal, fast, dt, this.settings);
      }
      this.stats.updateMs = performance.now() - before;
    }
    initialize() {
      const gl = this.gl, shaders = [];
      try {
        this.program = gl.createProgram();
        for (const [type, code] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
          const shader = gl.createShader(type); shaders.push(shader); gl.shaderSource(shader, code); gl.compileShader(shader);
          if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader)); gl.attachShader(this.program, shader);
        }
        gl.linkProgram(this.program);
        if (!gl.getProgramParameter(this.program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(this.program));
        this.uvLocation = gl.getAttribLocation(this.program, 'aUv'); this.uniforms = {};
        for (const key of ['Matrix', 'Origin', 'Direction', 'Side', 'BandCount', 'Density', 'Layer', 'Seed', 'Lfe', 'Low', 'Mid', 'High', 'Opacity', 'Intensity', 'FlowPhase', 'FlowDirection', 'MatchSpark'])
          this.uniforms[key] = gl.getUniformLocation(this.program, 'u' + key);
        this.uniforms.Bands = gl.getUniformLocation(this.program, 'uBands[0]');
        this.uniforms.Colors = gl.getUniformLocation(this.program, 'uColors[0]');
      } catch (e) { this.dispose(); throw e; }
      finally { for (const shader of shaders) gl.deleteShader(shader); }
    }
    buildMesh() {
      const gl = this.gl, [cols, rows] = budgets[this.settings.quality];
      const vertices = new Float32Array((cols + 1) * (rows + 1) * 2), indices = new Uint16Array(cols * rows * 6);
      for (let y = 0; y <= rows; y++) for (let x = 0; x <= cols; x++) {
        const k = (y * (cols + 1) + x) * 2; vertices[k] = x / cols; vertices[k + 1] = y / rows;
      }
      let i = 0;
      for (let y = 0; y < rows; y++) for (let x = 0; x < cols; x++) {
        const a = y * (cols + 1) + x, b = a + cols + 1;
        indices[i++] = a; indices[i++] = b; indices[i++] = a + 1; indices[i++] = a + 1; indices[i++] = b; indices[i++] = b + 1;
      }
      this.releaseMesh();
      this.mesh = { vertex: gl.createBuffer(), index: gl.createBuffer(), count: indices.length, vertices: vertices.length / 2, quality: this.settings.quality };
      if (!this.mesh.vertex || !this.mesh.index) { this.releaseMesh(); throw new Error('Cannot allocate Spectral Veil mesh buffers.'); }
      gl.bindBuffer(gl.ARRAY_BUFFER, this.mesh.vertex); gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.mesh.index); gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW); this.stats.meshBuilds++;
    }
    draw(matrix, scene) {
      const gl = this.gl, motion = this.motion, s = this.settings, before = performance.now();
      this.stats.drawCalls = 0; this.stats.vertices = 0;
      if (!motion || s.opacity <= 0 || s.intensity <= 0) return;
      const program = gl.getParameter(gl.CURRENT_PROGRAM), buffer = gl.getParameter(gl.ARRAY_BUFFER_BINDING), indices = gl.getParameter(gl.ELEMENT_ARRAY_BUFFER_BINDING);
      const blend = gl.isEnabled(gl.BLEND), depth = gl.isEnabled(gl.DEPTH_TEST), cull = gl.isEnabled(gl.CULL_FACE), mask = gl.getParameter(gl.DEPTH_WRITEMASK);
      const src = gl.getParameter(gl.BLEND_SRC_RGB), dst = gl.getParameter(gl.BLEND_DST_RGB), srcAlpha = gl.getParameter(gl.BLEND_SRC_ALPHA), dstAlpha = gl.getParameter(gl.BLEND_DST_ALPHA);
      try {
        if (!this.program) this.initialize(); if (!this.mesh || this.mesh.quality !== s.quality) this.buildMesh();
        const u = this.uniforms, palette = palettes[s.palette] || palettes.spectrum;
        gl.useProgram(this.program); gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA); gl.disable(gl.CULL_FACE); gl.disable(gl.DEPTH_TEST); gl.depthMask(false);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.mesh.vertex); gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.mesh.index);
        gl.enableVertexAttribArray(this.uvLocation); gl.vertexAttribPointer(this.uvLocation, 2, gl.FLOAT, false, 8, 0);
        gl.uniformMatrix4fv(u.Matrix, false, matrix); gl.uniform1f(u.BandCount, motion.count); gl.uniform1f(u.Density, s.density);
        gl.uniform1f(u.Opacity, s.opacity); gl.uniform1f(u.Intensity, s.intensity);
        gl.uniform1f(u.MatchSpark, s.palette === 'spark' ? 1 : 0); gl.uniform3fv(u.Colors, this.bandColors);
        gl.uniform3f(u.Low, ...palette[0]); gl.uniform3f(u.Mid, ...palette[1]); gl.uniform3f(u.High, ...palette[2]);
        for (const { speaker, channel, seed } of this.items) {
          if (scene.channelSuppressed(channel) || (speaker.code === 'LFE' && !document.getElementById('lfe').checked)) { this.hide(channel); continue; }
          const packet = motion.packets[channel]; let maximum = 0;
          for (let b = 0; b < motion.count; b++) maximum = Math.max(maximum, packet[b * 4]);
          if (maximum < .0001) continue;
          gl.uniform3f(u.Origin, ...speaker.position); gl.uniform3f(u.Direction, ...speaker.direction); gl.uniform3f(u.Side, ...speaker.side);
          gl.uniform4fv(u.Bands, packet); gl.uniform1f(u.Seed, seed); gl.uniform1f(u.Lfe, speaker.code === 'LFE' ? 1 : 0);
          gl.uniform1f(u.FlowPhase, motion.flowPhase[channel]);
          gl.uniform1f(u.FlowDirection, s.flowDirection === 'down' ? -1 : s.flowDirection === 'up' ? 1 : 0);
          for (let layer = 0; layer < budgets[s.quality][2]; layer++) {
            gl.uniform1f(u.Layer, layer); gl.drawElements(gl.TRIANGLES, this.mesh.count, gl.UNSIGNED_SHORT, 0);
            this.stats.drawCalls++; this.stats.vertices += this.mesh.vertices;
          }
        }
      } finally {
        gl.disableVertexAttribArray(this.uvLocation ?? 0); gl.useProgram(program); gl.bindBuffer(gl.ARRAY_BUFFER, buffer); gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indices);
        gl.blendFuncSeparate(src, dst, srcAlpha, dstAlpha); gl.depthMask(mask);
        if (blend) gl.enable(gl.BLEND); else gl.disable(gl.BLEND); if (depth) gl.enable(gl.DEPTH_TEST); else gl.disable(gl.DEPTH_TEST);
        if (cull) gl.enable(gl.CULL_FACE); else gl.disable(gl.CULL_FACE); this.stats.drawSubmitMs = performance.now() - before;
      }
    }
    releaseMesh() { if (this.mesh) { this.gl.deleteBuffer(this.mesh.vertex); this.gl.deleteBuffer(this.mesh.index); this.mesh = null; } }
    dispose() { this.releaseMesh(); if (this.program) this.gl.deleteProgram(this.program); this.program = null; }
  }
  return { defaults, presets, sanitize, style, hasSpark, hasVeil, BandMotion, smooth, Renderer };
})();
