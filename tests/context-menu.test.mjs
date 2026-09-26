import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const code = await readFile(new URL('../src/Mootify/wwwroot/js/context-menu.js', import.meta.url), 'utf8');
const { position } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));

function menu() {
    return {
        style: {},
        getBoundingClientRect: () => ({ width: 184, height: 140 }),
        querySelector: () => ({ focus: () => {} }),
    };
}

test('context menu follows pointer coordinates on a wide viewport', () => {
    globalThis.window = { innerWidth: 3000, innerHeight: 1000 };
    const element = menu();
    position(element, 850, 330, false);
    assert.equal(element.style.left, '850px');
    assert.equal(element.style.top, '330px');
});

test('context menu stays inside bottom and right edges', () => {
    globalThis.window = { innerWidth: 1000, innerHeight: 700 };
    const element = menu();
    position(element, 998, 698, false);
    assert.equal(element.style.left, '808px');
    assert.equal(element.style.top, '552px');
});

test('keyboard opening anchors to the focused options button', () => {
    globalThis.window = { innerWidth: 1000, innerHeight: 700 };
    globalThis.document = { activeElement: { getBoundingClientRect: () => ({ left: 450, bottom: 210 }) } };
    const element = menu();
    position(element, 0, 0, true);
    assert.equal(element.style.left, '450px');
    assert.equal(element.style.top, '210px');
});
