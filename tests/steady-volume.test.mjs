import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';

const code = await readFile(new URL('../src/Mootify/wwwroot/js/steady-volume-worklet.js', import.meta.url), 'utf8');
function fixture(enabled = true) {
    let Processor;
    vm.runInNewContext(code, {
        sampleRate: 48000,
        AudioWorkletProcessor: class { constructor() { this.port = {}; } },
        registerProcessor: (_, value) => { Processor = value; }
    });
    const processor = new Processor();
    processor.port.onmessage({ data: { enabled } });
    const run = (amplitude, seconds = 1) => {
        const input = [new Float32Array(128).fill(amplitude), new Float32Array(128).fill(-amplitude)];
        const output = [new Float32Array(128), new Float32Array(128)];
        for (let i = 0; i < seconds * 48000 / 128; i++) processor.process([input], [output]);
        return output;
    };
    return { processor, run };
}

test('quiet passages are lifted gradually and gain is bounded', () => {
    const { processor, run } = fixture();
    run(0.02, 0.5);
    assert.ok(processor.gain > 1 && processor.gain < 1.2);
    run(0.02, 20);
    assert.ok(processor.gain > 1.9 && processor.gain <= 2);
});

test('silence does not accumulate boost and resets clear previous-track gain', () => {
    const { processor, run } = fixture();
    run(0, 10);
    assert.equal(processor.gain, 1);
    run(0.02, 5);
    processor.port.onmessage({ data: { enabled: true } });
    assert.equal(processor.gain, 1);
    assert.equal(processor.power, 0);
});

test('sudden peaks stay below the ceiling and stereo polarity is preserved', () => {
    const { run } = fixture();
    run(0.02, 10);
    const output = run(2, 0.01);
    for (let i = 0; i < 128; i++) {
        assert.ok(Math.abs(output[0][i]) <= 10 ** (-1.5 / 20) + 1e-6);
        assert.equal(output[0][i], -output[1][i]);
    }
});

test('disabled leveling preserves quiet audio gain', () => {
    const { processor, run } = fixture(false);
    const output = run(0.02, 10);
    assert.equal(processor.gain, 1);
    assert.ok(Math.abs(output[0][0] - 0.02) < 1e-8);
});
