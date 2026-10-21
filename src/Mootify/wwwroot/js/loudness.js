let context, source, gain, processor, output, graphPromise;
let generation = 0, request;
let steadyEnabled = false;

export function prime(audio) {
    if (!globalThis.AudioContext && !globalThis.webkitAudioContext) return Promise.resolve(false);
    if (!context) {
        context = new (globalThis.AudioContext || globalThis.webkitAudioContext)();
        gain = context.createGain();
        output = context.createGain();
        output.gain.value = audio.volume;
        audio.volume = 1;
        source = context.createMediaElementSource(audio);
        source.connect(gain).connect(output).connect(context.destination);
        graphPromise = (async () => {
            try {
                await context.audioWorklet.addModule('./js/steady-volume-worklet.js');
                processor = new AudioWorkletNode(context, 'steady-volume');
            } catch (error) {
                console.warn('[mootify] Steady volume unavailable; using track gain only.', error);
            }
            return true;
        })();
    }
    context.resume().catch(() => {});
    return graphPromise;
}

export function setOutputVolume(volume) {
    if (!output) return false;
    output.gain.setTargetAtTime(volume, context.currentTime, 0.03);
    return true;
}

export function outputVolume(fallback) { return output ? output.gain.value : fallback; }

export function reset() {
    generation++;
    request?.abort();
    request = null;
    steadyEnabled = false;
    if (!gain) return;
    gain.gain.cancelScheduledValues(context.currentTime);
    gain.gain.setValueAtTime(1, context.currentTime);
    gain.disconnect();
    processor?.disconnect();
    gain.connect(output);
    processor?.port.postMessage({ enabled: false });
}

export function resetDynamics() { processor?.port.postMessage({ enabled: steadyEnabled }); }

export async function configure(audio, url, steady) {
    reset();
    const current = generation;
    if (!url) {
        context?.resume().catch(() => {});
        return;
    }
    if (!await prime(audio) || current !== generation) return;
    // User volume is downstream of analysis, so turning it down cannot trigger more gain.
    if (processor && steady) {
        steadyEnabled = true;
        gain.disconnect();
        gain.connect(processor).connect(output);
        processor.port.postMessage({ enabled: true });
    }
    request = new AbortController();
    try {
        const response = await fetch(url, { signal: request.signal, cache: 'no-store' });
        if (!response.ok || response.status === 204) return;
        const measurement = await response.json();
        if (current !== generation || !Number.isFinite(measurement.gainDb)) return;
        // Fade in late measurements; playback never waits for an unanalysed track.
        const value = 10 ** (Math.max(-40, Math.min(12, measurement.gainDb)) / 20);
        if (audio.paused) gain.gain.setValueAtTime(value, context.currentTime);
        else gain.gain.setTargetAtTime(value, context.currentTime, 0.5);
    } catch (error) {
        if (error.name !== 'AbortError') console.warn('[mootify] Loudness analysis unavailable.', error);
    }
}
