import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const code = await readFile(new URL('../src/Mootify/wwwroot/js/reorder-list.js', import.meta.url), 'utf8');
const { bind } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));

function fixture() {
    const handlers = new Map(), moves = [];
    const rows = ['a', 'b', 'c', 'd'].map((id, index) => {
        const classes = new Set();
        const row = {
            dataset: { reorderId: id }, draggable: true, classes,
            classList: { add: name => classes.add(name), remove: (...names) => names.forEach(n => classes.delete(n)) },
            getBoundingClientRect: () => ({ top: index * 40, height: 40 }),
            closest: () => row
        };
        return row;
    });
    const list = {
        querySelectorAll: () => rows,
        contains: row => rows.includes(row),
        addEventListener: (name, handler) => handlers.set(name, handler),
        removeEventListener: name => handlers.delete(name)
    };
    const binding = bind(list, { invokeMethodAsync: async (...args) => moves.push(args) });
    binding.update(true);
    async function emit(name, index, after = false, extra = {}) {
        const event = {
            target: rows[index], clientY: index * 40 + (after ? 30 : 10),
            dataTransfer: { setData() {} }, preventDefault() { this.prevented = true; }, ...extra
        };
        await handlers.get(name)?.(event);
        return event;
    }
    return { rows, moves, binding, emit, handlers };
}

for (const [from, over, after, target] of [
    [0, 2, false, 'b'], [0, 2, true, 'c'],
    [3, 1, false, 'b'], [3, 1, true, 'c'],
    [3, 0, false, 'a'], [0, 3, true, 'd']
]) {
    test(`drop ${from} at ${after ? 'bottom' : 'top'} of ${over} matches the insertion line`, async () => {
        const f = fixture();
        await f.emit('dragstart', from);
        assert.equal((await f.emit('dragover', over, after)).prevented, true);
        assert.deepEqual([...f.rows[over].classes], [after ? 'drop-after' : 'drop-before']);
        await f.emit('drop', over, after);
        assert.deepEqual(f.moves, [['MoveAsync', f.rows[from].dataset.reorderId, target]]);
        assert.ok(f.rows.every(r => r.classes.size === 0));
    });
}

test('dropping on either side of the source or its adjacent gap does not move it', async () => {
    for (const [over, after] of [[1, false], [1, true], [0, true], [2, false]]) {
        const f = fixture();
        await f.emit('dragstart', 1);
        await f.emit('dragover', over, after);
        assert.ok(f.rows.every(r => r.classes.size === 0));
        await f.emit('drop', over, after);
        assert.deepEqual(f.moves, []);
    }
});

test('crossing children keeps the marker; leaving, cancelling and disabling clear it', async () => {
    const f = fixture();
    await f.emit('dragstart', 0);
    await f.emit('dragover', 2);
    await f.emit('dragleave', 2, false, { relatedTarget: f.rows[2] });
    assert.equal(f.rows[2].classes.size, 1);
    await f.emit('dragleave', 2, false, { relatedTarget: null });
    assert.equal(f.rows[2].classes.size, 0);
    await f.emit('dragover', 2);
    await f.emit('dragend', 0);
    assert.equal(f.rows[2].classes.size, 0);
    await f.emit('dragstart', 0);
    await f.emit('dragover', 2);
    f.binding.update(false);
    await f.emit('drop', 2);
    assert.equal(f.rows[2].classes.size, 0);
    assert.deepEqual(f.moves, []);
    f.binding.dispose();
    assert.equal(f.handlers.size, 0);
});

test('external drags and disabled lists do not accept drops', async () => {
    const f = fixture();
    assert.equal((await f.emit('dragover', 2)).prevented, undefined);
    await f.emit('drop', 2);
    f.binding.update(false);
    await f.emit('dragstart', 0);
    await f.emit('drop', 2);
    assert.deepEqual(f.moves, []);
});
