import React, { useRef, useState } from 'react';
import type { SpriteStatus } from '../types';
import { ThumbnailPopup } from './ThumbnailPopup';

interface SeekBarProps {
  currentTime: number;
  duration: number;
  disabled: boolean;
  spriteStatus: SpriteStatus | null;
  onSeek: (time: number) => void;
}

export const SeekBar: React.FC<SeekBarProps> = ({
  currentTime,
  duration,
  disabled,
  spriteStatus,
  onSeek,
}) => {
  const trackRef = useRef<HTMLDivElement>(null);
  const lastSeekRef = useRef<number>(0);
  const [dragging, setDragging] = useState(false);
  const [hoverInfo, setHoverInfo] = useState<{ x: number; time: number; width: number } | null>(null);

  const computeTimeAtClientX = (clientX: number): { time: number; x: number; width: number } | null => {
    const el = trackRef.current;
    if (!el || duration <= 0) return null;
    const rect = el.getBoundingClientRect();
    const x = Math.max(0, Math.min(rect.width, clientX - rect.left));
    const ratio = rect.width > 0 ? x / rect.width : 0;
    return { time: ratio * duration, x, width: rect.width };
  };

  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (disabled || duration <= 0) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    setDragging(true);
    const info = computeTimeAtClientX(e.clientX);
    if (info) {
      onSeek(info.time);
      lastSeekRef.current = performance.now();
    }
  };

  const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (disabled || duration <= 0) {
      setHoverInfo(null);
      return;
    }
    const info = computeTimeAtClientX(e.clientX);
    if (info) {
      setHoverInfo(info);
      if (dragging) {
        const now = performance.now();
        if (now - lastSeekRef.current >= 50) {
          onSeek(info.time);
          lastSeekRef.current = now;
        }
      }
    }
  };

  const handlePointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragging) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    setDragging(false);
    const info = computeTimeAtClientX(e.clientX);
    if (info) {
      onSeek(info.time);
    }
  };

  const pct = duration > 0 ? Math.min(100, Math.max(0, (currentTime / duration) * 100)) : 0;

  return (
    <div
      ref={trackRef}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerLeave={() => {
        if (!dragging) setHoverInfo(null);
      }}
      className={`relative flex h-5 w-full select-none items-center ${
        disabled ? 'cursor-default opacity-50' : 'cursor-pointer'
      }`}
      aria-label="Barre de lecture"
    >
      {hoverInfo && !disabled && (
        <ThumbnailPopup
          time={hoverInfo.time}
          x={hoverInfo.x}
          containerWidth={hoverInfo.width}
          spriteStatus={spriteStatus}
        />
      )}

      <div className="relative h-1.5 w-full overflow-hidden rounded-full bg-[#22262E] ring-1 ring-[#2E3440]">
        <div
          className="h-full bg-[#3B82F6] transition-[width] duration-75"
          style={{ width: `${pct}%` }}
        />
      </div>

      <div
        className="pointer-events-none absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-[#3B82F6] bg-[#EDF0F5] shadow"
        style={{ left: `${pct}%` }}
      />
    </div>
  );
};
