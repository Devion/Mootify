// Runs on the audio thread, including when the tab is in the background.
class SteadyVolumeProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.power = 0;
        this.gain = 1;
        this.limiter = 1;
        this.enabled = false;
        this.port.onmessage = ({ data }) => {
            this.enabled = !!data.enabled;
            this.power = 0;
            this.gain = this.limiter = 1;
        };
    }
    process(inputs, outputs) {
        const input = inputs[0], output = outputs[0];
        if (!input?.length) return true;
        const envelope = Math.exp(-1 / (sampleRate * 0.4));
        const release = Math.exp(-1 / (sampleRate * 0.15));
        const attack = Math.exp(-1 / (sampleRate * 0.5));
        const recovery = Math.exp(-1 / (sampleRate * 4));
        const ceiling = 10 ** (-1.5 / 20);
        for (let i = 0; i < output[0].length; i++) {
            let power = 0, peak = 0;
            for (const channel of input) {
                power += channel[i] ** 2;
                peak = Math.max(peak, Math.abs(channel[i]));
            }
            this.power = envelope * this.power + (1 - envelope) * power / input.length;
            // Silence gate and ±6 dB bounds prevent quiet intros/noise from being inflated.
            const target = this.enabled && this.power > 0.00001
                ? Math.max(0.5, Math.min(2, 0.1 / Math.sqrt(this.power))) : 1;
            const smoothing = target < this.gain ? attack : recovery;
            this.gain = target + smoothing * (this.gain - target);
            const limit = Math.min(1, ceiling / Math.max(peak * this.gain, 1e-12));
            this.limiter = limit < this.limiter ? limit : limit + release * (this.limiter - limit);
            for (let c = 0; c < output.length; c++)
                output[c][i] = (input[c]?.[i] ?? 0) * this.gain * this.limiter;
        }
        return true;
    }
}
registerProcessor('steady-volume', SteadyVolumeProcessor);
