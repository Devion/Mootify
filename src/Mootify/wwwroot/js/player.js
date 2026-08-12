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
    // Metadata arrived, so the browser really can decode this one.
    audio.addEventListener('loadedmetadata', () => clearTimeout(stallTimer));

    audio.addEventListener('error', () => {
        // canPlayType is a guess and browsers get it wrong. If a native decode fails and we
        // have a transcoded copy to fall back on, take it once — silently, mid-track, so the
        // listener sees a hiccup instead of a dead player.
        const code = audio.error ? audio.error.code : 0;
        if (switchToFallback('media error ' + code)) return;

        dotnet?.invokeMethodAsync('OnError', 'media error code ' + code);
    });

    return audio;
}

export function init(dotnetRef) {
    dotnet = dotnetRef;
    ensureAudio();
    primeNotificationAudio();
}

// Chrome, Firefox and Edge decode FLAC; Safari's support has been patchy and version
// dependent. Ask the browser rather than sniffing the user agent, and treat "maybe" as no —
// a silent failure mid-song is worse than transcoding something we didn't need to.
function supportsFlac() {
    const el = ensureAudio();
    return el.canPlayType('audio/flac') === 'probably'
        || el.canPlayType('audio/x-flac') === 'probably';
}

// Set when a native decode fails or stalls, so the retry doesn't loop.
let usedFallback = false;
let stallTimer = null;

// How long to give a native file to produce metadata before assuming the browser can't
// actually decode it.
const STALL_MS = 6000;

function switchToFallback(reason) {
    const el = ensureAudio();
    if (!el._fallbackUrl || usedFallback) return false;

    usedFallback = true;
    clearTimeout(stallTimer);

    const at = el.currentTime || 0;
    el.src = el._fallbackUrl;
    el.currentTime = at;
    el.play().catch(() => {});

    // So the play bar says MP3 rather than claiming FLAC while streaming a transcode.
    dotnet?.invokeMethodAsync('OnFallbackUsed');

    console.info('[mootify] falling back to transcoded audio:', reason);
    return true;
}

export function play(url, fallbackUrl) {
    const el = ensureAudio();

    // fallbackUrl is only supplied for tracks stored in something other than MP3.
    const useFallback = !!fallbackUrl && !supportsFlac();
    usedFallback = useFallback;
    el._fallbackUrl = fallbackUrl || null;

    el.src = useFallback ? fallbackUrl : url;
    el.play().catch(err => dotnet?.invokeMethodAsync('OnError', String(err)));

    // Decided before a single byte moved — the browser told us it can't decode this container.
    if (useFallback) dotnet?.invokeMethodAsync('OnFallbackUsed');

    clearTimeout(stallTimer);

    // canPlayType answers about the container, not the file. A 24-bit/192kHz FLAC is a
    // format Chrome claims it can play and then can't decode: it loads for ever, produces
    // no metadata, and fires no error — so there's nothing to react to except the silence.
    // If nothing has arrived by now, assume the worst and take the transcoded copy.
    if (!useFallback && fallbackUrl) {
        stallTimer = setTimeout(() => {
            if (el.readyState < 1) switchToFallback('no metadata after ' + STALL_MS + 'ms');
        }, STALL_MS);
    }

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
