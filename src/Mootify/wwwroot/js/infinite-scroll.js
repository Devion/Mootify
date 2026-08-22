// The bottom edge of a list, watched.
//
// One IntersectionObserver per sentinel, handed back as an object so the component that made it
// can also throw it away — a circuit that navigates away and leaves an observer behind holds a
// DOM node alive for as long as the tab is open.

export function watch(sentinel, owner, margin) {
    let disposed = false;

    // The root has to be the element that actually scrolls, not the viewport. The app scrolls
    // inside .shell__main (the play bar and the sidebar don't move), and a clipping ancestor
    // trims the intersection *before* rootMargin is applied — so with the default root the
    // lookahead below would be silently worth nothing and rows would arrive at the exact moment
    // somebody had already scrolled past where they go. Found rather than named, so this keeps
    // working if the shell is restyled.
    const root = scrollParent(sentinel);

    const observer = new IntersectionObserver(entries => {
        if (!disposed && entries.some(e => e.isIntersecting)) reached();
    }, { root, rootMargin: `${margin}px 0px` });

    observer.observe(sentinel);

    function reached() {
        // Fire-and-forget: the component decides whether it is in a position to load anything,
        // and a call rejected by a circuit that has gone away must not take the observer with it.
        owner.invokeMethodAsync("ReachedAsync").catch(() => { });
    }

    return {
        // An IntersectionObserver reports a *change*. Appending rows above a sentinel that never
        // left the screen is not one, so a short list in a tall window would load a single page
        // and stop. The component re-checks after every render, which is what keeps it going
        // until the viewport is actually full.
        recheck: () => {
            if (disposed) return;

            const view = root ? root.getBoundingClientRect()
                              : { top: 0, bottom: window.innerHeight };
            const box = sentinel.getBoundingClientRect();

            if (box.top < view.bottom + margin && box.bottom > view.top - margin) reached();
        },

        dispose: () => {
            disposed = true;
            observer.disconnect();
        },
    };
}

// Null means "the viewport", which is what IntersectionObserver wants for that case anyway.
function scrollParent(element) {
    for (let p = element.parentElement; p; p = p.parentElement) {
        const overflow = getComputedStyle(p).overflowY;
        if (overflow === "auto" || overflow === "scroll") return p;
    }

    return null;
}
