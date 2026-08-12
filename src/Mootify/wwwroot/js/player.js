// One audio element for the whole session.
//
// Two rules that everything here exists to protect:
//   1. This element is created once and never destroyed. iOS Safari will not autoplay the
//      next track unless playback started from a user gesture in the *same* element, so
//      swapping elements between tracks silently breaks gapless playback on iPhones.
//   2. The cowbell never touches this element. It gets its own (see notifications.js) —
//      playing a notification through the music element stops the music.

let audio = null;
let dotnet = null;
let duckTimer = null;
let userVolume = 0.8;

function ensureAudio() {
    if (audio) return audio;

    audio = document.getElementById('mootify-audio');
    if (!audio) {
        audio = document.createElement('audio');
        audio.id = 'mootify-audio';
        audio.preload = 'metadata';
        document.body.appendChild(audio);
    }

    audio.addEventListener('timeupdate', () => {
        // ~4/sec from the browser; throttle so we aren't rendering the circuit to death.
        const now = performance.now();
        if (now - (audio._lastTick || 0) < 500) return;
        audio._lastTick = now;
        dotnet?.invokeMethodAsync('OnTimeUpdate', audio.currentTime, audio.duration || 0);
    });

    audio.addEventListener('ended', () => dotnet?.invokeMethodAsync('OnEnded'));
    audio.addEventListener('play', () => dotnet?.invokeMethodAsync('OnPlayStateChanged', true));
    audio.addEventListener('pause', () => dotnet?.invokeMethodAsync('OnPlayStateChanged', false));
    audio.addEventListener('error', () => {
        const code = audio.error ? audio.error.code : 0;
        dotnet?.invokeMethodAsync('OnError', 'media error code ' + code);
    });

    return audio;
}

export function init(dotnetRef) {
    dotnet = dotnetRef;
    ensureAudio();
    primeNotificationAudio();
}

export function play(url) {
    const el = ensureAudio();
    el.src = url;
    el.play().catch(err => dotnet?.invokeMethodAsync('OnError', String(err)));
    updateMediaSession();
}

export function resume() {
    ensureAudio().play().catch(err => dotnet?.invokeMethodAsync('OnError', String(err)));
}

export function pause() {
    ensureAudio().pause();
}

export function seek(seconds) {
    const el = ensureAudio();
    if (isFinite(seconds)) el.currentTime = seconds;
}

export function setVolume(volume) {
    userVolume = volume;
    ensureAudio().volume = volume;
}

// Duck rather than stop, and ramp rather than jump — an instant volume change reads as a glitch.
export function duck(toVolume, durationMs) {
    const el = ensureAudio();
    if (el.paused) return;

    clearTimeout(duckTimer);
    ramp(el, toVolume, 150);

    duckTimer = setTimeout(() => ramp(el, userVolume, 300), durationMs);
}

function ramp(el, target, ms) {
    const from = el.volume;
    const start = performance.now();

    function step(now) {
        const t = Math.min(1, (now - start) / ms);
        el.volume = Math.max(0, Math.min(1, from + (target - from) * t));
        if (t < 1) requestAnimationFrame(step);
    }

    requestAnimationFrame(step);
}

// Lock-screen and headset controls. Cheap, and the difference between a web page and
// something you'd actually use on a phone.
export function setMetadata(title, artist, album, artworkUrl) {
    if (!('mediaSession' in navigator)) return;

    navigator.mediaSession.metadata = new MediaMetadata({
        title: title || '',
        artist: artist || '',
        album: album || '',
        artwork: artworkUrl ? [{ src: artworkUrl, sizes: '512x512', type: 'image/png' }] : [],
    });
}

function updateMediaSession() {
    if (!('mediaSession' in navigator) || !dotnet) return;

    navigator.mediaSession.setActionHandler('play', () => resume());
    navigator.mediaSession.setActionHandler('pause', () => pause());
    navigator.mediaSession.setActionHandler('nexttrack', () => dotnet.invokeMethodAsync('OnEnded'));
}

// Browsers block audio until a user gesture. Login and the first play click are both
// gestures — prime the cowbell element there, or the first notification of the session
// is swallowed and the feature looks broken.
function primeNotificationAudio() {
    const prime = () => {
        const bell = document.getElementById('mootify-cowbell');
        if (bell) {
            bell.muted = true;
            bell.play().then(() => {
                bell.pause();
                bell.currentTime = 0;
                bell.muted = false;
            }).catch(() => { /* still blocked; the next gesture will retry */ });
        }
        document.removeEventListener('pointerdown', prime);
        document.removeEventListener('keydown', prime);
    };

    document.addEventListener('pointerdown', prime, { once: true });
    document.addEventListener('keydown', prime, { once: true });
}

// Space toggles play unless you're typing. This check has to happen in JS: routing it
// through the circuit adds enough latency to swallow keystrokes in a text box.
export function registerHotkeys(dotnetRef) {
    document.addEventListener('keydown', e => {
        if (e.code !== 'Space') return;

        const el = document.activeElement;
        const typing = el && (
            el.tagName === 'INPUT' ||
            el.tagName === 'TEXTAREA' ||
            el.isContentEditable
        );
        if (typing) return;

        e.preventDefault();
        dotnetRef.invokeMethodAsync('ToggleFromHotkey');
    });
}
