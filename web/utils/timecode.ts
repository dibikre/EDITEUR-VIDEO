export function formatTimecode(seconds: number, withMillis = false): string {
  if (!Number.isFinite(seconds) || seconds < 0) {
    return withMillis ? '00:00:00.0' : '00:00:00';
  }
  const hrs = Math.floor(seconds / 3600);
  const mins = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);
  const tenths = Math.floor((seconds % 1) * 10);

  const hh = String(hrs).padStart(2, '0');
  const mm = String(mins).padStart(2, '0');
  const ss = String(secs).padStart(2, '0');

  return withMillis ? `${hh}:${mm}:${ss}.${tenths}` : `${hh}:${mm}:${ss}`;
}

export function snapToNearestKeyframe(time: number, keyframes: number[], thresholdSeconds = 0.75): number {
  if (keyframes.length === 0) return time;
  let nearest = keyframes[0] ?? time;
  let bestDist = Math.abs(time - nearest);
  for (const kf of keyframes) {
    const d = Math.abs(time - kf);
    if (d < bestDist) {
      bestDist = d;
      nearest = kf;
    }
  }
  return bestDist <= thresholdSeconds ? nearest : time;
}

export function chooseRulerStep(pixelsPerSecond: number): number {
  const candidates = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
  for (const step of candidates) {
    if (step * pixelsPerSecond >= 75) {
      return step;
    }
  }
  return 600;
}
