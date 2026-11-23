import React, { useEffect, useRef, useState } from 'react';
import type { Segment, SpriteStatus } from '../types';
import { chooseRulerStep, formatTimecode, snapToNearestKeyframe } from '../utils/timecode';
import { ThumbnailPopup } from './ThumbnailPopup';

interface TimelineProps {
  duration: number;
  currentTime: number;
  segments: Segment[];
  selectedSegmentId: string | null;
  keyframes: number[];
  snapToKeyframes: boolean;
  spriteStatus: SpriteStatus | null;
  zoom: number;
  onZoomChange: (nextZoom: number) => void;
  onSeek: (time: number) => void;
  onSelectSegment: (id: string) => void;
  onMoveMarker: (boundaryIndex: number, newTime: number, commit: boolean) => void;
}

const RULER_HEIGHT = 24;
const FILMSTRIP_TOP = 26;
const FILMSTRIP_HEIGHT = 68;

export const Timeline: React.FC<TimelineProps> = ({
  duration,
  currentTime,
  segments,
  selectedSegmentId,
  keyframes,
  snapToKeyframes,
  spriteStatus,
  zoom,
  onZoomChange,
  onSeek,
  onSelectSegment,
  onMoveMarker,
}) => {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const sheetImagesRef = useRef<Map<string, HTMLImageElement>>(new Map());

  const [viewportWidth, setViewportWidth] = useState(960);
  const [scrollLeft, setScrollLeft] = useState(0);
  const [dragMode, setDragMode] = useState<
    | { kind: 'playhead' }
    | { kind: 'marker'; boundaryIndex: number }
    | null
  >(null);
  const [hoverInfo, setHoverInfo] = useState<{ x: number; time: number; width: number } | null>(null);
  const [loadedSheetsTick, setLoadedSheetsTick] = useState(0);

  // Preload sprite sheet images as they become available
  useEffect(() => {
    if (!spriteStatus) return;
    for (const url of spriteStatus.sheetUrls) {
      if (!sheetImagesRef.current.has(url)) {
        const img = new Image();
        img.onload = () => setLoadedSheetsTick((t) => t + 1);
        img.src = url;
        sheetImagesRef.current.set(url, img);
      }
    }
  }, [spriteStatus]);

  // Reset scroll & cached images when video changes
  useEffect(() => {
    setScrollLeft(0);
    if (duration === 0) {
      sheetImagesRef.current.clear();
    }
  }, [duration]);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) {
        setViewportWidth(Math.max(300, Math.floor(entry.contentRect.width)));
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const totalWidth = Math.max(viewportWidth, Math.round(viewportWidth * zoom));
  const maxScroll = Math.max(0, totalWidth - viewportWidth);
  const clampedScroll = Math.min(scrollLeft, maxScroll);
  const pps = duration > 0 ? totalWidth / duration : 1;

  const timeToViewportX = (t: number) => t * pps - clampedScroll;
  const viewportXToTime = (vx: number) => Math.max(0, Math.min(duration, (vx + clampedScroll) / pps));

  // Draw timeline canvas (ruler + virtualized filmstrip + segments + markers + playhead)
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    const height = RULER_HEIGHT + FILMSTRIP_HEIGHT + 8;
    canvas.width = Math.floor(viewportWidth * dpr);
    canvas.height = Math.floor(height * dpr);
    canvas.style.width = `${viewportWidth}px`;
    canvas.style.height = `${height}px`;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    // Background
    ctx.fillStyle = '#121417';
    ctx.fillRect(0, 0, viewportWidth, height);

    // Ruler band
    ctx.fillStyle = '#1A1D23';
    ctx.fillRect(0, 0, viewportWidth, RULER_HEIGHT);
    ctx.strokeStyle = '#2E3440';
    ctx.beginPath();
    ctx.moveTo(0, RULER_HEIGHT);
    ctx.lineTo(viewportWidth, RULER_HEIGHT);
    ctx.stroke();

    if (duration <= 0) {
      ctx.fillStyle = '#666E7D';
      ctx.font = '12px "Plus Jakarta Sans", sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('Ouvrez une vidéo pour afficher la timeline et les vignettes', viewportWidth / 2, FILMSTRIP_TOP + 38);
      return;
    }

    // Ruler ticks
    const step = chooseRulerStep(pps);
    const startTime = viewportXToTime(0);
    const endTime = viewportXToTime(viewportWidth);
    const firstTick = Math.floor(startTime / step) * step;

    ctx.fillStyle = '#9AA3B2';
    ctx.strokeStyle = '#3E4554';
    ctx.font = '10px "JetBrains Mono", monospace';
    ctx.textAlign = 'left';

    for (let t = firstTick; t <= endTime + step; t += step) {
      if (t < 0 || t > duration) continue;
      const x = Math.round(timeToViewportX(t)) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, RULER_HEIGHT - 8);
      ctx.lineTo(x, RULER_HEIGHT);
      ctx.stroke();
      ctx.fillText(formatTimecode(t, step < 1), x + 4, 15);
    }

    // Virtualized filmstrip from sprite sheets
    if (spriteStatus && spriteStatus.sheetUrls.length > 0) {
      const thumbAspect = spriteStatus.thumbWidth / Math.max(1, spriteStatus.thumbHeight);
      const drawW = Math.max(48, Math.round(FILMSTRIP_HEIGHT * thumbAspect));
      const perSheet = spriteStatus.columns * spriteStatus.rows;

      const startTile = Math.floor(clampedScroll / drawW);
      const endTile = Math.ceil((clampedScroll + viewportWidth) / drawW);

      for (let tile = startTile; tile <= endTile; tile++) {
        const tileWorldX = tile * drawW;
        const t = (tileWorldX + drawW * 0.5) / pps;
        if (t < 0 || t > duration) continue;

        const thumbIdx = Math.min(spriteStatus.count - 1, Math.max(0, Math.floor(t / spriteStatus.intervalSeconds)));
        const sheetIdx = Math.floor(thumbIdx / perSheet);
        const cellIdx = thumbIdx % perSheet;
        const col = cellIdx % spriteStatus.columns;
        const row = Math.floor(cellIdx / spriteStatus.columns);
        const sheetUrl = spriteStatus.sheetUrls[sheetIdx];
        const img = sheetUrl ? sheetImagesRef.current.get(sheetUrl) : undefined;

        const vx = tileWorldX - clampedScroll;
        if (img && img.complete && img.naturalWidth > 0) {
          ctx.drawImage(
            img,
            col * spriteStatus.thumbWidth,
            row * spriteStatus.thumbHeight,
            spriteStatus.thumbWidth,
            spriteStatus.thumbHeight,
            vx,
            FILMSTRIP_TOP,
            drawW,
            FILMSTRIP_HEIGHT,
          );
        } else {
          ctx.fillStyle = '#1A1D23';
          ctx.fillRect(vx, FILMSTRIP_TOP, drawW - 1, FILMSTRIP_HEIGHT);
        }
      }
    } else {
      ctx.fillStyle = '#1A1D23';
      ctx.fillRect(0, FILMSTRIP_TOP, viewportWidth, FILMSTRIP_HEIGHT);
    }

    // Segments overlay
    segments.forEach((seg, idx) => {
      const x1 = Math.max(0, timeToViewportX(seg.start));
      const x2 = Math.min(viewportWidth, timeToViewportX(seg.end));
      if (x2 <= 0 || x1 >= viewportWidth) return;

      const w = x2 - x1;
      const isSelected = seg.id === selectedSegmentId;

      if (!seg.enabled) {
        ctx.fillStyle = 'rgba(10, 12, 16, 0.72)';
        ctx.fillRect(x1, FILMSTRIP_TOP, w, FILMSTRIP_HEIGHT);
      } else if (isSelected) {
        ctx.fillStyle = 'rgba(59, 130, 246, 0.22)';
        ctx.fillRect(x1, FILMSTRIP_TOP, w, FILMSTRIP_HEIGHT);
        ctx.strokeStyle = '#3B82F6';
        ctx.lineWidth = 2;
        ctx.strokeRect(x1 + 1, FILMSTRIP_TOP + 1, Math.max(0, w - 2), FILMSTRIP_HEIGHT - 2);
        ctx.lineWidth = 1;
      } else {
        ctx.fillStyle = idx % 2 === 0 ? 'rgba(255, 255, 255, 0.04)' : 'rgba(59, 130, 246, 0.06)';
        ctx.fillRect(x1, FILMSTRIP_TOP, w, FILMSTRIP_HEIGHT);
      }

      if (w > 46) {
        ctx.fillStyle = seg.enabled ? '#EDF0F5' : '#666E7D';
        ctx.font = '600 11px "Plus Jakarta Sans", sans-serif';
        ctx.textAlign = 'left';
        ctx.fillText(seg.name, x1 + 6, FILMSTRIP_TOP + 16, w - 12);
      }
    });

    // Keyframe ticks (subtle bottom dots when zoomed in)
    if (snapToKeyframes && keyframes.length > 0 && pps > 6) {
      ctx.fillStyle = 'rgba(245, 158, 11, 0.75)';
      for (const kf of keyframes) {
        if (kf < startTime || kf > endTime) continue;
        const kx = timeToViewportX(kf);
        ctx.fillRect(kx - 1, FILMSTRIP_TOP + FILMSTRIP_HEIGHT - 4, 2, 4);
      }
    }

    // Cut markers between segments
    for (let i = 0; i < segments.length - 1; i++) {
      const boundary = segments[i]!.end;
      const mx = timeToViewportX(boundary);
      if (mx < -6 || mx > viewportWidth + 6) continue;

      ctx.strokeStyle = '#F59E0B';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(mx, RULER_HEIGHT);
      ctx.lineTo(mx, FILMSTRIP_TOP + FILMSTRIP_HEIGHT);
      ctx.stroke();
      ctx.lineWidth = 1;

      ctx.fillStyle = '#F59E0B';
      ctx.fillRect(mx - 4, RULER_HEIGHT, 8, 8);
    }

    // Playhead
    const px = timeToViewportX(currentTime);
    if (px >= -2 && px <= viewportWidth + 2) {
      ctx.strokeStyle = '#EF4444';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(px, 0);
      ctx.lineTo(px, height);
      ctx.stroke();
      ctx.lineWidth = 1;

      ctx.fillStyle = '#EF4444';
      ctx.beginPath();
      ctx.moveTo(px - 6, 0);
      ctx.lineTo(px + 6, 0);
      ctx.lineTo(px, 9);
      ctx.closePath();
      ctx.fill();
    }
  }, [
    viewportWidth,
    duration,
    currentTime,
    segments,
    selectedSegmentId,
    keyframes,
    snapToKeyframes,
    spriteStatus,
    zoom,
    clampedScroll,
    pps,
    loadedSheetsTick,
  ]);

  const findNearestMarker = (vx: number): number | null => {
    for (let i = 0; i < segments.length - 1; i++) {
      const mx = timeToViewportX(segments[i]!.end);
      if (Math.abs(mx - vx) <= 7) {
        return i;
      }
    }
    return null;
  };

  const handlePointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (duration <= 0) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const vx = e.clientX - rect.left;
    const vy = e.clientY - rect.top;

    const markerIdx = findNearestMarker(vx);
    if (markerIdx !== null) {
      e.currentTarget.setPointerCapture(e.pointerId);
      setDragMode({ kind: 'marker', boundaryIndex: markerIdx });
      return;
    }

    const clickedTime = viewportXToTime(vx);
    if (vy >= FILMSTRIP_TOP) {
      const hitSeg = segments.find((s) => clickedTime >= s.start && clickedTime <= s.end);
      if (hitSeg) {
        onSelectSegment(hitSeg.id);
      }
    }

    e.currentTarget.setPointerCapture(e.pointerId);
    setDragMode({ kind: 'playhead' });
    onSeek(clickedTime);
  };

  const handlePointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (duration <= 0) {
      setHoverInfo(null);
      return;
    }
    const rect = e.currentTarget.getBoundingClientRect();
    const vx = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
    let t = viewportXToTime(vx);

    setHoverInfo({ x: vx, time: t, width: rect.width });

    if (dragMode?.kind === 'playhead') {
      onSeek(t);
    } else if (dragMode?.kind === 'marker') {
      if (snapToKeyframes) {
        t = snapToNearestKeyframe(t, keyframes);
      }
      onMoveMarker(dragMode.boundaryIndex, t, false);
    }
  };

  const handlePointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!dragMode) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    if (dragMode.kind === 'marker') {
      const rect = e.currentTarget.getBoundingClientRect();
      const vx = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
      let t = viewportXToTime(vx);
      if (snapToKeyframes) {
        t = snapToNearestKeyframe(t, keyframes);
      }
      onMoveMarker(dragMode.boundaryIndex, t, true);
    }
    setDragMode(null);
  };

  const handleWheel = (e: React.WheelEvent<HTMLDivElement>) => {
    if (duration <= 0) return;
    if (e.ctrlKey || e.metaKey) {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.25 : 0.8;
      const nextZoom = Math.max(1, Math.min(16, zoom * factor));
      const rect = e.currentTarget.getBoundingClientRect();
      const cursorVx = e.clientX - rect.left;
      const timeAtCursor = viewportXToTime(cursorVx);
      onZoomChange(nextZoom);

      const nextTotalWidth = Math.max(viewportWidth, Math.round(viewportWidth * nextZoom));
      const nextPps = nextTotalWidth / duration;
      const nextScroll = Math.max(0, Math.min(nextTotalWidth - viewportWidth, timeAtCursor * nextPps - cursorVx));
      setScrollLeft(nextScroll);
    } else if (maxScroll > 0) {
      e.preventDefault();
      const delta = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
      setScrollLeft((s) => Math.max(0, Math.min(maxScroll, s + delta)));
    }
  };

  return (
    <div
      ref={containerRef}
      onWheel={handleWheel}
      className="relative w-full select-none"
    >
      {hoverInfo && duration > 0 && (
        <ThumbnailPopup
          time={hoverInfo.time}
          x={hoverInfo.x}
          containerWidth={hoverInfo.width}
          spriteStatus={spriteStatus}
        />
      )}

      <canvas
        ref={canvasRef}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerLeave={() => {
          if (!dragMode) setHoverInfo(null);
        }}
        className="block w-full cursor-crosshair rounded border border-[#242832]"
      />

      {maxScroll > 0 && (
        <input
          type="range"
          min={0}
          max={maxScroll}
          value={clampedScroll}
          onChange={(e) => setScrollLeft(Number(e.target.value))}
          className="mt-1 h-1.5 w-full cursor-pointer accent-[#3B82F6]"
          aria-label="Défilement horizontal de la timeline"
        />
      )}
    </div>
  );
};
