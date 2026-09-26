// Client coordinates keep a right-click menu beside the pointer even inside a scrolled table.
export function position(menu, x, y, keyboard) {
    if (keyboard && document.activeElement) {
        const anchor = document.activeElement.getBoundingClientRect();
        x = anchor.left;
        y = anchor.bottom;
    }
    const bounds = menu.getBoundingClientRect();
    const padding = 8;
    menu.style.left = Math.max(padding, Math.min(x, window.innerWidth - bounds.width - padding)) + 'px';
    menu.style.top = Math.max(padding, Math.min(y, window.innerHeight - bounds.height - padding)) + 'px';
    menu.querySelector('button')?.focus({ preventScroll: true });
}
