import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const code = await readFile(new URL('../src/Mootify/wwwroot/js/notifications.js', import.meta.url), 'utf8');
let moduleId = 0;

function loadModule() {
    return import('data:text/javascript;base64,' +
        Buffer.from(code + `\n// fixture ${moduleId++}`).toString('base64'));
}

async function fixture(src = '/audio/cowbell.mp3') {
    const storage = new Map();
    globalThis.localStorage = {
        getItem: key => storage.get(key) ?? null,
        setItem: (key, value) => storage.set(key, value),
    };
    const audio = {
        dataset: {}, currentTime: 4, plays: 0,
        getAttribute: name => name === 'src' ? src : null,
        setAttribute: (name, value) => { if (name === 'src') src = value; },
        async play() { this.plays++; },
    };
    globalThis.document = { getElementById: id => id === 'mootify-cowbell' ? audio : null };
    const notifications = await loadModule();
    return { notifications, audio, storage };
}

test('the cowbell remains the default until the fifth logo click', async () => {
    const { notifications, audio, storage } = await fixture();
    for (let clicks = 0; clicks < 4; clicks++) {
        assert.equal(notifications.clickLogo(), false);
        assert.equal(await notifications.playCowbell(false, 0.3), true);
        assert.equal(audio.getAttribute('src'), '/audio/cowbell.mp3');
    }
    assert.equal(notifications.clickLogo(), true);
    assert.equal(storage.get('mootify.sound.extremeCowMode'), '1');
    assert.equal(audio.currentTime, 0);
});

test('cow mode randomly selects each of the three moo files for successive notifications', async () => {
    const { notifications, audio } = await fixture();
    for (let clicks = 0; clicks < 5; clicks++) notifications.clickLogo();
    const random = Math.random;
    try {
        for (const [value, expected] of [
            [0, 'moo.mp3'], [0.34, 'moo1.mp3'], [0.999, 'moo2.mp3'], [0.01, 'moo.mp3'],
        ]) {
            Math.random = () => value;
            assert.equal(await notifications.playCowbell(false, 0.3), true);
            assert.equal(audio.getAttribute('src'), '/audio/' + expected);
        }
    } finally {
        Math.random = random;
    }
    assert.equal(audio.plays, 4);
});

test('cow mode survives a module reload and still respects the mute setting', async () => {
    const { notifications, audio } = await fixture();
    for (let clicks = 0; clicks < 5; clicks++) notifications.clickLogo();
    notifications.setMuted(true);
    const reloaded = await loadModule();
    assert.equal(await reloaded.playCowbell(false, 0.3), false);
    assert.equal(audio.plays, 0);
    reloaded.setMuted(false);
    assert.equal(await reloaded.playCowbell(false, 0.3), true);
    assert.match(audio.getAttribute('src'), /^\/audio\/moo[12]?\.mp3$/);
});

test('normal mode preserves the configured notification sound', async () => {
    const { notifications, audio, storage } = await fixture('/audio/custom.mp3');
    assert.equal(await notifications.playCowbell(false, 0.3), true);
    assert.equal(audio.getAttribute('src'), '/audio/custom.mp3');
    storage.set('mootify.sound.extremeCowMode', '1');
    await notifications.playCowbell(false, 0.3);
    storage.delete('mootify.sound.extremeCowMode');
    await notifications.playCowbell(false, 0.3);
    assert.equal(audio.getAttribute('src'), '/audio/custom.mp3');
});

test('blocked notification playback returns false without affecting music', async () => {
    const { notifications, audio } = await fixture();
    audio.play = async () => { throw new Error('autoplay blocked'); };
    assert.equal(await notifications.playCowbell(false, 0.3), false);
});
