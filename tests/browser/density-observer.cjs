/* Runs in the page. Test-owned event history; production retains only current state. */
function installObserver() {
    window.densitySamples = [];
    window.densityEvents = [];
    window.addEventListener('danmuku-operation', (event) => {
        densityEvents.push(event.detail);
        if (densityEvents.length > 200) densityEvents.shift();
        if (event.detail.phase === 'committed' && event.detail.reason === 'load')
            densitySamples.push(event.detail);
    });
    window.measureDensityOperation = (reason, change) => new Promise((resolve, reject) => {
        let identity;
        const started = performance.now();
        let triggeredAt = started;
        const seeked = () => { triggeredAt = performance.now(); };
        if (reason === "seek") window.addEventListener("seeked", seeked, true);
        const timer = setTimeout(() => done(Error('Missing density commit: ' + reason)), 15000);
        function done(error, state) {
            clearTimeout(timer); window.removeEventListener('danmuku-operation', listen);
            window.removeEventListener('seeked', seeked, true);
            if (error) reject(error); else resolve({ ...state, inputAt: started, sampledAt: triggeredAt, elapsed: state.committedAt - triggeredAt });
        }
        function listen({ detail: state }) {
            if (!identity && state.reason === reason && state.phase === 'computing') identity = state;
            if (!identity || ['playbackId', 'operationId', 'generation'].some(k => state[k] !== identity[k])) return;
            if (state.phase === 'committed') done(null, state);
            if (state.phase === 'cancelled' || state.phase === 'failed') done(Error('Operation ' + state.phase));
        }
        window.addEventListener('danmuku-operation', listen);
        try { change(); } catch (error) { done(error); }
    });
}
const p95 = samples => samples.map(s => s.elapsed).sort((a, b) => a - b)[Math.ceil(samples.length * .95) - 1];
const assertLatency = (samples, budget) => {
    if (samples.length !== 20 || samples.some(s => s.phase !== 'committed') || p95(samples) > budget)
        throw Error('Density latency rejected: ' + p95(samples));
};
module.exports = { installObserver, p95, assertLatency };
