// The cowbell.
//
// It plays through its own <audio> element, never the music one. Routing a notification
// through the player would stop whatever is playing — this separation is the entire point
// of this file.

const MUTE_KEY = 'mootify.sound.muted';
const COW_MODE_KEY = 'mootify.sound.extremeCowMode';
const MOO_SOUNDS = ['/audio/moo.mp3', '/audio/moo1.mp3', '/audio/moo2.mp3'];
let logoClicks = 0;

export function clickLogo() {
    if (++logoClicks < 5) return false;
    logoClicks = 0;
    localStorage.setItem(COW_MODE_KEY, '1');
    return true;
}

// Read straight from localStorage so the mute setting applies on first paint, before the
// circuit has connected and told us the user's stored preference.
export function isMuted() {
    return localStorage.getItem(MUTE_KEY) === '1';
}

export function setMuted(muted) {
    localStorage.setItem(MUTE_KEY, muted ? '1' : '0');
    return muted;
}

function bell() {
    return document.getElementById('mootify-cowbell');
}

export async function playCowbell(duckMusic, duckTo) {
    if (isMuted()) return false;

    const el = bell();
    if (!el) return false;

    el.dataset.normalSound ??= el.getAttribute('src');
    const sound = localStorage.getItem(COW_MODE_KEY) === '1'
        ? MOO_SOUNDS[Math.floor(Math.random() * MOO_SOUNDS.length)]
        : el.dataset.normalSound;
    if (el.getAttribute('src') !== sound) el.setAttribute('src', sound);

    try {
        el.currentTime = 0;
        await el.play();
    } catch {
        // Autoplay still blocked — no gesture yet this session. The badge already updated,
        // so the user isn't missing the notification itself.
        return false;
    }

    if (duckMusic) {
        const ms = (isFinite(el.duration) && el.duration > 0 ? el.duration : 1.5) * 1000;
        const player = await import('./player.js');
        player.duck(duckTo, ms);
    }

    return true;
}
