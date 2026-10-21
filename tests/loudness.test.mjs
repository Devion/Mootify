import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const code = await readFile(new URL('../src/Mootify/wwwroot/js/loudness.js', import.meta.url), 'utf8');
let sequence = 0;
async function fixture({ unsupported = false, workletFails = false } = {}) {
    const nodes = [], requests = [], messages = [];
    const node = () => {
        const value = {
            gain: { value: 1, cancelScheduledValues() {},
                setValueAtTime(v) { this.value = v; }, setTargetAtTime(v) { this.value = v; } },
            connections: [],
            connect(target) { this.connections.push(target); return target; },
            disconnect() { this.connections = []; }
        };
        nodes.push(value);
        return value;
    };
    globalThis.AudioContext = unsupported ? undefined : class {
        currentTime = 0;
        destination = {};
        audioWorklet = { addModule: async () => { if (workletFails) throw new Error('unavailable'); } };
        createGain() { return node(); }
        createMediaElementSource() { return node(); }
        resume() { return Promise.resolve(); }
    };
    globalThis.AudioWorkletNode = class {
        constructor() { return { ...node(), port: { postMessage: m => messages.push(m) } }; }
    };
    globalThis.fetch = (url, options) => new Promise(resolve => requests.push({ url, options, resolve }));
    const module = await import('data:text/javascript;base64,' + Buffer.from(code + `\n// ${sequence++}`).toString('base64'));
    const audio = { volume: 0.4, paused: false };
    const flush = () => new Promise(resolve => setImmediate(resolve));
    const respond = (index, gainDb) => requests[index].resolve({ ok: true, status: 200, json: async () => ({ gainDb }) });
    return { module, audio, nodes, requests, messages, flush, respond };
}

test('track gain preserves independent user volume and one audio source', async () => {
    const f = await fixture();
    const pending = f.module.configure(f.audio, '/first', true);
    await f.flush();
    f.respond(0, -6);
    await pending;
    assert.ok(Math.abs(f.nodes[0].gain.value - 10 ** (-6 / 20)) < 1e-8);
    assert.equal(f.nodes[1].gain.value, 0.4);
    assert.equal(f.audio.volume, 1);
    f.module.setOutputVolume(0.2);
    assert.equal(f.nodes[1].gain.value, 0.2);
    await f.module.prime(f.audio);
    assert.equal(f.nodes.length, 4);
    assert.equal(f.nodes[1].gain.value, 0.2);
});

test('late measurements from a skipped track cannot change the new track', async () => {
    const f = await fixture();
    const first = f.module.configure(f.audio, '/first', true);
    await f.flush();
    const second = f.module.configure(f.audio, '/second', false);
    await f.flush();
    assert.equal(f.requests[0].options.signal.aborted, true);
    f.respond(1, -3);
    await second;
    f.respond(0, 12);
    await first;
    assert.ok(Math.abs(f.nodes[0].gain.value - 10 ** (-3 / 20)) < 1e-8);
    assert.equal(f.nodes[0].connections[0], f.nodes[1]);
});

test('turning normalization off resets gain and bypasses steady processing', async () => {
    const f = await fixture();
    const pending = f.module.configure(f.audio, '/first', true);
    await f.flush();
    f.respond(0, -10);
    await pending;
    await f.module.configure(f.audio, null, false);
    assert.equal(f.nodes[0].gain.value, 1);
    assert.equal(f.nodes[0].connections[0], f.nodes[1]);
    assert.equal(f.nodes[1].gain.value, 0.4);
});

test('unsupported Web Audio leaves native playback untouched', async () => {
    const f = await fixture({ unsupported: true });
    await f.module.configure(f.audio, '/first', true);
    assert.equal(f.audio.volume, 0.4);
    assert.equal(f.requests.length, 0);
    assert.equal(f.module.setOutputVolume(0.2), false);
});

test('missing worklet still applies measured gain', async t => {
    const warning = t.mock.method(console, 'warn', () => {});
    const f = await fixture({ workletFails: true });
    const pending = f.module.configure(f.audio, '/first', true);
    await f.flush();
    f.respond(0, -6);
    await pending;
    assert.ok(f.nodes[0].gain.value < 1);
    assert.equal(f.nodes[0].connections[0], f.nodes[1]);
    assert.equal(warning.mock.callCount(), 1);
});

test('seeking clears dynamics without changing measured gain or user volume', async () => {
    const f = await fixture();
    const pending = f.module.configure(f.audio, '/first', true);
    await f.flush();
    f.respond(0, -6);
    await pending;
    const gain = f.nodes[0].gain.value;
    f.module.resetDynamics();
    assert.deepEqual(f.messages.at(-1), { enabled: true });
    assert.equal(f.nodes[0].gain.value, gain);
    assert.equal(f.nodes[1].gain.value, 0.4);
});
