// Keep pointer feedback in the browser so it never waits for a server round trip.
export function bind(list, owner) {
    let enabled = false, source = null, marked = null, pending = false;
    const rows = () => [...list.querySelectorAll('[data-reorder-id]')];
    const rowAt = event => event.target.closest('[data-reorder-id]');
    function clearMarker() {
        marked?.classList.remove('drop-before', 'drop-after');
        marked = null;
    }
    function reset() { clearMarker(); source = null; }
    function destination(event) {
        const target = rowAt(event);
        const items = rows();
        const from = items.indexOf(source), over = items.indexOf(target);
        if (!enabled || pending || from < 0 || over < 0 || source.draggable !== true) return null;
        const box = target.getBoundingClientRect();
        const after = event.clientY >= box.top + box.height / 2;
        const boundary = over + (after ? 1 : 0);
        const to = boundary - (from < boundary ? 1 : 0);
        return { target, after, item: items[to], changed: to !== from };
    }
    function start(event) {
        reset();
        const row = rowAt(event);
        if (!enabled || pending || !row || !list.contains(row) || !row.draggable) return;
        source = row;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', row.dataset.reorderId);
        // Selected text can span neighboring rows; always preview only this row.
        const box = row.getBoundingClientRect();
        event.dataTransfer.setDragImage(row, event.clientX - box.left, event.clientY - box.top);
    }
    function over(event) {
        const dest = destination(event);
        clearMarker();
        if (!dest) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        if (dest.changed) {
            marked = dest.target;
            marked.classList.add(dest.after ? 'drop-after' : 'drop-before');
        }
    }
    function leave(event) {
        if (!list.contains(event.relatedTarget)) clearMarker();
    }
    async function drop(event) {
        const dest = destination(event);
        const id = source?.dataset.reorderId;
        reset();
        if (!dest) return;
        event.preventDefault();
        if (!dest.changed) return;
        pending = true;
        try { await owner.invokeMethodAsync('MoveAsync', id, dest.item.dataset.reorderId); }
        catch (error) { console.error('Could not reorder list', error); }
        finally { pending = false; }
    }
    list.addEventListener('dragstart', start);
    list.addEventListener('dragover', over);
    list.addEventListener('dragleave', leave);
    list.addEventListener('drop', drop);
    list.addEventListener('dragend', reset);
    return {
        update(value) { enabled = value; if (!enabled || (source && !list.contains(source))) reset(); },
        dispose() {
            reset();
            list.removeEventListener('dragstart', start);
            list.removeEventListener('dragover', over);
            list.removeEventListener('dragleave', leave);
            list.removeEventListener('drop', drop);
            list.removeEventListener('dragend', reset);
        }
    };
}
