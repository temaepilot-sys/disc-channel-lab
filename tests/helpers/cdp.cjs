const fs = require('node:fs');
class Cdp {
  constructor(url) {
    this.next = 0; this.pending = new Map(); this.events = [];
    this.socket = new WebSocket(url);
    this.ready = new Promise((resolve, reject) => { this.socket.onopen = resolve; this.socket.onerror = reject; });
    this.socket.onmessage = event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const item = this.pending.get(message.id); if (!item) return;
        clearTimeout(item.timeout); this.pending.delete(message.id);
        if (message.error) item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message.result);
      } else if (['Runtime.exceptionThrown', 'Log.entryAdded'].includes(message.method)) this.events.push(message);
    };
  }
  async send(method, params = {}) {
    await this.ready; const id = ++this.next;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { this.pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 45000);
      this.pending.set(id, { resolve, reject, timeout }); this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || JSON.stringify(result.exceptionDetails));
    return result.result?.value;
  }
  async screenshot(path) {
    const result = await this.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    fs.writeFileSync(path, Buffer.from(result.data, 'base64'));
  }
  close() { this.socket.close(); }
}
async function connect(endpoint, port = 9236) {
  const url = JSON.parse(fs.readFileSync(endpoint)).url;
  const target = await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' }).then(r => r.json());
  const client = new Cdp(target.webSocketDebuggerUrl);
  await client.send('Runtime.enable'); await client.send('Page.enable'); await client.send('Log.enable');
  await client.send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
  try {
    for (let i = 0; i < 100; i++) {
      if (await client.evaluate('!!window.lab')) { await client.evaluate('window.lab.settingsReady'); return client; }
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Viewer page did not initialize; verify the endpoint belongs to the running test host.');
  } catch (error) { client.close(); throw error; }
}
module.exports = { Cdp, connect };
