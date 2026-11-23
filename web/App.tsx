import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  Download,
  Film,
  FolderOpen,
  Magnet,
  Pause,
  Play,
  Redo2,
  Scissors,
  Trash2,
  Undo2,
  Volume2,
  VolumeX,
  ZoomIn,
  ZoomOut,
  Maximize2,
  Loader2,
  ArrowLeftToLine,
  ArrowRightToLine,
} from 'lucide-react';
import type {
  ExportMode,
  ExportStatusResponse,
  MediaInfo,
  Segment,
  SpriteStatus,
} from './types';
import { SeekBar } from './components/SeekBar';
import { Timeline } from './components/Timeline';
import { SegmentsPanel } from './components/SegmentsPanel';
import { formatTimecode, snapToNearestKeyframe } from './utils/timecode';

const MIN_SEGMENT_DURATION = 0.1;
const MAX_HISTORY = 200;

function createSegmentId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `seg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
}

function cloneSegments(list: Segment[]): Segment[] {
  return list.map((s) => ({ ...s }));
}

export function App() {
  const videoRef = useRef<HTMLVideoElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const historyRef = useRef<{ past: Segment[][]; future: Segment[][] }>({ past: [], future: [] });

  const [media, setMedia] = useState<MediaInfo | null>(null);
  const [streamUrl, setStreamUrl] = useState<string | null>(null);
  const [spriteStatus, setSpriteStatus] = useState<SpriteStatus | null>(null);

  const [isLoadingMedia, setIsLoadingMedia] = useState(false);
  const [isDraggingFile, setIsDraggingFile] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [statusNotice, setStatusNotice] = useState<string | null>(null);

  // Playback state
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [volume, setVolume] = useState(1);
  const [isMuted, setIsMuted] = useState(false);
  const [playbackRate, setPlaybackRate] = useState(1);

  // Editing state
  const [segments, setSegments] = useState<Segment[]>([]);
  const segmentsRef = useRef<Segment[]>([]);
  segmentsRef.current = segments;
  const [selectedSegmentId, setSelectedSegmentId] = useState<string | null>(null);
  const [snapToKeyframes, setSnapToKeyframes] = useState(true);
  const [zoom, setZoom] = useState(1);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);

  // Export state
  const [exportMode, setExportMode] = useState<ExportMode>('fast');
  const [crf, setCrf] = useState(18);
  const [template, setTemplate] = useState('{nom}_part{NN}');
  const [exportJobId, setExportJobId] = useState<string | null>(null);
  const [exportStatus, setExportStatus] = useState<ExportStatusResponse | null>(null);
  const [isExporting, setIsExporting] = useState(false);

  const duration = media?.duration ?? 0;

  const syncHistoryFlags = () => {
    setCanUndo(historyRef.current.past.length > 0);
    setCanRedo(historyRef.current.future.length > 0);
  };

  const commitSegments = useCallback((next: Segment[], prevSnapshot?: Segment[]) => {
    const snapshot = prevSnapshot ?? segmentsRef.current;
    historyRef.current.past.push(cloneSegments(snapshot));
    if (historyRef.current.past.length > MAX_HISTORY) {
      historyRef.current.past.shift();
    }
    historyRef.current.future = [];
    syncHistoryFlags();
    segmentsRef.current = next;
    setSegments(next);
  }, []);

  const handleUndo = useCallback(() => {
    const prev = historyRef.current.past.pop();
    if (!prev) return;
    historyRef.current.future.push(cloneSegments(segmentsRef.current));
    syncHistoryFlags();
    segmentsRef.current = prev;
    setSegments(prev);
    if (!prev.some((s) => s.id === selectedSegmentId)) {
      setSelectedSegmentId(prev[0]?.id ?? null);
    }
  }, [selectedSegmentId]);

  const handleRedo = useCallback(() => {
    const next = historyRef.current.future.pop();
    if (!next) return;
    historyRef.current.past.push(cloneSegments(segmentsRef.current));
    syncHistoryFlags();
    segmentsRef.current = next;
    setSegments(next);
    if (!next.some((s) => s.id === selectedSegmentId)) {
      setSelectedSegmentId(next[0]?.id ?? null);
    }
  }, [selectedSegmentId]);

  // Smooth playhead animation frame loop while playing
  useEffect(() => {
    if (!isPlaying) return;
    let rafId = 0;
    const tick = () => {
      const vid = videoRef.current;
      if (vid) {
        setCurrentTime(vid.currentTime);
      }
      rafId = requestAnimationFrame(tick);
    };
    rafId = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(rafId);
  }, [isPlaying]);

  // Poll sprite sheet generation while video is loaded and sprites aren't done
  useEffect(() => {
    if (!media) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const poll = async () => {
      try {
        const res = await fetch(`/api/videos/${media.id}/sprites`);
        if (!res.ok) return;
        const data = (await res.json()) as SpriteStatus;
        if (cancelled) return;
        setSpriteStatus(data);
        if (!data.done) {
          timer = setTimeout(poll, 700);
        }
      } catch {
        // Retry silently if transient
        if (!cancelled) {
          timer = setTimeout(poll, 1200);
        }
      }
    };

    void poll();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [media]);

  // Poll export job status
  useEffect(() => {
    if (!exportJobId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const poll = async () => {
      try {
        const res = await fetch(`/api/export/${exportJobId}/status`);
        if (!res.ok) return;
        const data = (await res.json()) as ExportStatusResponse;
        if (cancelled) return;
        setExportStatus(data);
        if (data.done) {
          setIsExporting(false);
        } else {
          timer = setTimeout(poll, 400);
        }
      } catch {
        if (!cancelled) {
          timer = setTimeout(poll, 800);
        }
      }
    };

    void poll();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [exportJobId]);

  const applyLoadedMedia = (loadedMedia: MediaInfo, url: string) => {
    setMedia(loadedMedia);
    setStreamUrl(url);
    setSpriteStatus(null);
    setIsPlaying(false);
    setCurrentTime(0);
    setZoom(1);
    setExportJobId(null);
    setExportStatus(null);
    setIsExporting(false);
    setErrorMessage(null);
    setStatusNotice(null);

    const initialSegment: Segment = {
      id: createSegmentId(),
      name: 'Segment 1',
      start: 0,
      end: loadedMedia.duration,
      enabled: true,
    };
    historyRef.current = { past: [], future: [] };
    syncHistoryFlags();
    setSegments([initialSegment]);
    setSelectedSegmentId(initialSegment.id);
  };

  const handleUploadFile = async (file: File) => {
    setIsLoadingMedia(true);
    setErrorMessage(null);
    setStatusNotice(`Chargement de ${file.name}…`);
    try {
      const formData = new FormData();
      formData.append('video', file);
      const res = await fetch('/api/videos/upload', {
        method: 'POST',
        body: formData,
      });
      const data = (await res.json()) as { media?: MediaInfo; streamUrl?: string; error?: string };
      if (!res.ok || !data.media || !data.streamUrl) {
        throw new Error(data.error || 'Impossible de charger cette vidéo.');
      }
      applyLoadedMedia(data.media, data.streamUrl);
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Erreur lors du chargement vidéo.');
      setStatusNotice(null);
    } finally {
      setIsLoadingMedia(false);
    }
  };

  const handleLoadDemo = async () => {
    setIsLoadingMedia(true);
    setErrorMessage(null);
    setStatusNotice('Génération de la vidéo de démonstration…');
    try {
      const res = await fetch('/api/videos/demo', { method: 'POST' });
      const data = (await res.json()) as { media?: MediaInfo; streamUrl?: string; error?: string };
      if (!res.ok || !data.media || !data.streamUrl) {
        throw new Error(data.error || 'Échec de génération de la vidéo démo.');
      }
      applyLoadedMedia(data.media, data.streamUrl);
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Erreur de génération démo.');
      setStatusNotice(null);
    } finally {
      setIsLoadingMedia(false);
    }
  };

  const seekTo = useCallback(
    (targetTime: number) => {
      if (duration <= 0) return;
      const clamped = Math.max(0, Math.min(duration, targetTime));
      setCurrentTime(clamped);
      const vid = videoRef.current;
      if (vid && Number.isFinite(clamped)) {
        vid.currentTime = clamped;
      }
    },
    [duration],
  );

  const togglePlayPause = useCallback(() => {
    const vid = videoRef.current;
    if (!vid || !media) return;
    if (vid.paused) {
      void vid.play().catch(() => {});
    } else {
      vid.pause();
    }
  }, [media]);

  const stepFrame = useCallback(
    (deltaFrames: number) => {
      const vid = videoRef.current;
      if (!vid || !media) return;
      if (!vid.paused) vid.pause();
      const frameStep = 1 / Math.max(1, media.fps || 30);
      seekTo(vid.currentTime + deltaFrames * frameStep);
    },
    [media, seekTo],
  );

  // Sync video audio/rate properties
  useEffect(() => {
    const vid = videoRef.current;
    if (!vid) return;
    vid.volume = volume;
    vid.muted = isMuted;
    vid.playbackRate = playbackRate;
  }, [volume, isMuted, playbackRate, streamUrl]);

  // Split segment at playhead
  const handleSplit = useCallback(() => {
    if (!media || duration <= 0) return;
    const rawCut = snapToKeyframes ? snapToNearestKeyframe(currentTime, media.keyframes) : currentTime;
    const idx = segments.findIndex((s) => rawCut >= s.start && rawCut <= s.end);
    if (idx === -1) return;
    const target = segments[idx]!;
    if (rawCut - target.start < MIN_SEGMENT_DURATION || target.end - rawCut < MIN_SEGMENT_DURATION) {
      setStatusNotice('Position trop proche d’un bord (durée minimale 0,1 s).');
      return;
    }

    const left: Segment = {
      ...target,
      end: rawCut,
    };
    const right: Segment = {
      id: createSegmentId(),
      name: `Segment ${segments.length + 1}`,
      start: rawCut,
      end: target.end,
      enabled: target.enabled,
    };

    const next = [...segments.slice(0, idx), left, right, ...segments.slice(idx + 1)];
    commitSegments(next);
    setSelectedSegmentId(right.id);
    setStatusNotice(`Coupe ajoutée à ${formatTimecode(rawCut, true)}`);
  }, [media, duration, snapToKeyframes, currentTime, segments, commitSegments]);

  // Set In (I)
  const handleSetIn = useCallback(() => {
    if (!media || duration <= 0) return;
    const cut = snapToKeyframes ? snapToNearestKeyframe(currentTime, media.keyframes) : currentTime;
    const idx = segments.findIndex((s) => cut >= s.start && cut <= s.end);
    if (idx === -1) return;
    const target = segments[idx]!;

    if (idx === 0) {
      if (cut - target.start < MIN_SEGMENT_DURATION || target.end - cut < MIN_SEGMENT_DURATION) {
        setStatusNotice('Impossible de rogner le début ici (durée minimale 0,1 s).');
        return;
      }
      const trimmedOut: Segment = {
        id: createSegmentId(),
        name: 'Intro rognée',
        start: 0,
        end: cut,
        enabled: false,
      };
      const kept: Segment = {
        ...target,
        start: cut,
      };
      const next = [trimmedOut, kept, ...segments.slice(1)];
      commitSegments(next);
      setSelectedSegmentId(kept.id);
      setStatusNotice(`Point d'entrée fixé à ${formatTimecode(cut, true)}`);
    } else {
      const prevSeg = segments[idx - 1]!;
      if (cut - prevSeg.start < MIN_SEGMENT_DURATION || target.end - cut < MIN_SEGMENT_DURATION) {
        setStatusNotice('Déplacement impossible (durée minimale 0,1 s).');
        return;
      }
      const next = cloneSegments(segments);
      next[idx - 1]!.end = cut;
      next[idx]!.start = cut;
      commitSegments(next);
      setStatusNotice(`Point d'entrée ajusté à ${formatTimecode(cut, true)}`);
    }
  }, [media, duration, snapToKeyframes, currentTime, segments, commitSegments]);

  // Set Out (O)
  const handleSetOut = useCallback(() => {
    if (!media || duration <= 0) return;
    const cut = snapToKeyframes ? snapToNearestKeyframe(currentTime, media.keyframes) : currentTime;
    const idx = segments.findIndex((s) => cut >= s.start && cut <= s.end);
    if (idx === -1) return;
    const target = segments[idx]!;

    if (idx === segments.length - 1) {
      if (cut - target.start < MIN_SEGMENT_DURATION || target.end - cut < MIN_SEGMENT_DURATION) {
        setStatusNotice('Impossible de rogner la fin ici (durée minimale 0,1 s).');
        return;
      }
      const kept: Segment = {
        ...target,
        end: cut,
      };
      const trimmedOut: Segment = {
        id: createSegmentId(),
        name: 'Fin rognée',
        start: cut,
        end: target.end,
        enabled: false,
      };
      const next = [...segments.slice(0, idx), kept, trimmedOut];
      commitSegments(next);
      setSelectedSegmentId(kept.id);
      setStatusNotice(`Point de sortie fixé à ${formatTimecode(cut, true)}`);
    } else {
      const nextSeg = segments[idx + 1]!;
      if (cut - target.start < MIN_SEGMENT_DURATION || nextSeg.end - cut < MIN_SEGMENT_DURATION) {
        setStatusNotice('Déplacement impossible (durée minimale 0,1 s).');
        return;
      }
      const next = cloneSegments(segments);
      next[idx]!.end = cut;
      next[idx + 1]!.start = cut;
      commitSegments(next);
      setStatusNotice(`Point de sortie ajusté à ${formatTimecode(cut, true)}`);
    }
  }, [media, duration, snapToKeyframes, currentTime, segments, commitSegments]);

  // Remove cut marker (merge selected segment with previous or next)
  const handleRemoveMarker = useCallback(() => {
    if (segments.length <= 1) return;
    const idx = segments.findIndex((s) => s.id === selectedSegmentId);
    const mergeIdx = idx > 0 ? idx - 1 : 0;
    const left = segments[mergeIdx]!;
    const right = segments[mergeIdx + 1]!;
    const merged: Segment = {
      ...left,
      end: right.end,
      enabled: left.enabled || right.enabled,
    };
    const next = [...segments.slice(0, mergeIdx), merged, ...segments.slice(mergeIdx + 2)];
    commitSegments(next);
    setSelectedSegmentId(merged.id);
    setStatusNotice('Marqueur supprimé et segments fusionnés.');
  }, [segments, selectedSegmentId, commitSegments]);

  // Dragging marker on timeline
  const markerDragStartSnapshotRef = useRef<Segment[] | null>(null);
  const handleMoveMarker = useCallback(
    (boundaryIndex: number, newTime: number, commit: boolean) => {
      setSegments((current) => {
        if (!markerDragStartSnapshotRef.current) {
          markerDragStartSnapshotRef.current = cloneSegments(current);
        }
        const left = current[boundaryIndex];
        const right = current[boundaryIndex + 1];
        if (!left || !right) return current;

        const minT = left.start + MIN_SEGMENT_DURATION;
        const maxT = right.end - MIN_SEGMENT_DURATION;
        const clamped = Math.max(minT, Math.min(maxT, newTime));

        const next = cloneSegments(current);
        next[boundaryIndex]!.end = clamped;
        next[boundaryIndex + 1]!.start = clamped;

        if (commit) {
          const snapshot = markerDragStartSnapshotRef.current;
          markerDragStartSnapshotRef.current = null;
          if (snapshot) {
            historyRef.current.past.push(snapshot);
            if (historyRef.current.past.length > MAX_HISTORY) {
              historyRef.current.past.shift();
            }
            historyRef.current.future = [];
            syncHistoryFlags();
          }
        }
        return next;
      });
    },
    [],
  );

  const handleToggleSegment = useCallback(
    (id: string) => {
      const next = segments.map((s) => (s.id === id ? { ...s, enabled: !s.enabled } : s));
      commitSegments(next);
    },
    [segments, commitSegments],
  );

  const handleRenameSegment = useCallback(
    (id: string, name: string) => {
      const next = segments.map((s) => (s.id === id ? { ...s, name } : s));
      setSegments(next);
    },
    [],
  );

  const handleSelectSegment = useCallback(
    (id: string) => {
      setSelectedSegmentId(id);
      const seg = segments.find((s) => s.id === id);
      if (seg) {
        seekTo(seg.start);
      }
    },
    [segments, seekTo],
  );

  const handleExport = async () => {
    if (!media) return;
    const enabledSegments = segments.filter((s) => s.enabled);
    if (enabledSegments.length === 0) {
      setErrorMessage('Aucun segment activé à exporter.');
      return;
    }

    setErrorMessage(null);
    setIsExporting(true);
    setExportStatus(null);

    try {
      const payload = {
        videoId: media.id,
        mode: exportMode,
        crf,
        template,
        segments: enabledSegments.map((seg, i) => ({
          id: seg.id,
          name: seg.name,
          start: seg.start,
          end: seg.end,
          index: i + 1,
        })),
      };

      const res = await fetch('/api/export', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      const data = (await res.json()) as { jobId?: string; error?: string };
      if (!res.ok || !data.jobId) {
        throw new Error(data.error || 'Impossible de démarrer l’export.');
      }
      setExportJobId(data.jobId);
    } catch (err) {
      setIsExporting(false);
      setErrorMessage(err instanceof Error ? err.message : 'Erreur au lancement de l’export.');
    }
  };

  const handleCancelExport = async () => {
    if (!exportJobId) return;
    try {
      await fetch(`/api/export/${exportJobId}/cancel`, { method: 'POST' });
    } catch {
      // Polling will update status
    }
  };

  // Global keyboard shortcuts matching SPEC §5.2 & §5.6
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
        return;
      }

      if (e.code === 'Space') {
        e.preventDefault();
        togglePlayPause();
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault();
        if (e.shiftKey) {
          seekTo(currentTime - 5);
        } else {
          stepFrame(-1);
        }
      } else if (e.key === 'ArrowRight') {
        e.preventDefault();
        if (e.shiftKey) {
          seekTo(currentTime + 5);
        } else {
          stepFrame(1);
        }
      } else if (e.key === 'Home') {
        e.preventDefault();
        seekTo(0);
      } else if (e.key === 'End') {
        e.preventDefault();
        seekTo(duration);
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') {
        e.preventDefault();
        if (e.shiftKey) {
          handleRedo();
        } else {
          handleUndo();
        }
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'y') {
        e.preventDefault();
        handleRedo();
      } else if (!e.ctrlKey && !e.metaKey && !e.altKey) {
        const lower = e.key.toLowerCase();
        if (lower === 's') {
          e.preventDefault();
          handleSplit();
        } else if (lower === 'i') {
          e.preventDefault();
          handleSetIn();
        } else if (lower === 'o') {
          e.preventDefault();
          handleSetOut();
        } else if (lower === 'e' && selectedSegmentId) {
          e.preventDefault();
          handleToggleSegment(selectedSegmentId);
        }
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [
    togglePlayPause,
    seekTo,
    stepFrame,
    currentTime,
    duration,
    handleUndo,
    handleRedo,
    handleSplit,
    handleSetIn,
    handleSetOut,
    handleToggleSegment,
    selectedSegmentId,
  ]);

  const mediaSummary = useMemo(() => {
    if (!media) return null;
    return `${media.width}×${media.height} • ${media.fps} fps • ${media.videoCodec.toUpperCase()}${
      media.audioCodec ? ` / ${media.audioCodec.toUpperCase()}` : ''
    }`;
  }, [media]);

  const thumbnailStatusText = useMemo(() => {
    if (!media || !spriteStatus) return null;
    if (spriteStatus.error) return 'Vignettes indisponibles';
    if (spriteStatus.done) return `${spriteStatus.readySheets.length} planche(s) prête(s)`;
    return `Vignettes : ${Math.round(spriteStatus.progress * 100)}%`;
  }, [media, spriteStatus]);

  return (
    <div
      className="flex min-h-screen flex-col bg-[#121417] text-[#EDF0F5]"
      onDragOver={(e) => {
        e.preventDefault();
        setIsDraggingFile(true);
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) setIsDraggingFile(false);
      }}
      onDrop={(e) => {
        e.preventDefault();
        setIsDraggingFile(false);
        const file = e.dataTransfer.files?.[0];
        if (file) {
          void handleUploadFile(file);
        }
      }}
    >
      <input
        ref={fileInputRef}
        type="file"
        accept="video/*,.mkv,.avi,.mov,.mp4,.webm"
        className="hidden"
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) {
            void handleUploadFile(file);
          }
          e.target.value = '';
        }}
      />

      {/* 1. Top Toolbar */}
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-[#2E3440] bg-[#1A1D23] px-3 py-2">
        <div className="flex flex-wrap items-center gap-2">
          <div className="mr-2 flex items-center gap-1.5 font-semibold tracking-tight text-[#EDF0F5]">
            <Scissors className="h-4 w-4 text-[#3B82F6]" />
            <span>VideoSplitter</span>
          </div>

          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            disabled={isLoadingMedia}
            className="inline-flex items-center gap-1.5 rounded border border-[#2E3440] bg-[#22262E] px-3 py-1.5 text-xs font-medium text-[#EDF0F5] hover:bg-[#2A2F3A] disabled:opacity-50"
          >
            <FolderOpen className="h-3.5 w-3.5" />
            Ouvrir
          </button>

          <button
            type="button"
            onClick={() => void handleLoadDemo()}
            disabled={isLoadingMedia}
            className="inline-flex items-center gap-1.5 rounded border border-[#2E3440] bg-[#22262E] px-3 py-1.5 text-xs font-medium text-[#EDF0F5] hover:bg-[#2A2F3A] disabled:opacity-50"
          >
            <Film className="h-3.5 w-3.5 text-[#60A5FA]" />
            Vidéo démo
          </button>

          <button
            type="button"
            onClick={() => void handleExport()}
            disabled={!media || isExporting || segments.filter((s) => s.enabled).length === 0}
            className="inline-flex items-center gap-1.5 rounded border border-[#3B82F6] bg-[#3B82F6] px-3 py-1.5 text-xs font-semibold text-white hover:bg-[#60A5FA] disabled:border-[#242832] disabled:bg-[#181B20] disabled:text-[#555C69]"
          >
            <Download className="h-3.5 w-3.5" />
            Exporter
          </button>

          <div className="mx-1.5 h-5 w-px bg-[#2E3440]" />

          <div className="flex items-center gap-3 text-xs">
            <span className="text-[#9AA3B2]">Mode :</span>
            <label className="inline-flex cursor-pointer items-center gap-1.5">
              <input
                type="radio"
                name="topExportMode"
                checked={exportMode === 'fast'}
                onChange={() => setExportMode('fast')}
                className="accent-[#3B82F6]"
              />
              <span>Rapide</span>
            </label>
            <label className="inline-flex cursor-pointer items-center gap-1.5">
              <input
                type="radio"
                name="topExportMode"
                checked={exportMode === 'precise'}
                onChange={() => setExportMode('precise')}
                className="accent-[#3B82F6]"
              />
              <span>Précis</span>
            </label>
          </div>
        </div>

        <div className="flex items-center gap-4 text-xs text-[#9AA3B2]">
          {statusNotice && <span className="text-[#60A5FA]">{statusNotice}</span>}
          {thumbnailStatusText && <span>{thumbnailStatusText}</span>}
          {mediaSummary && (
            <span className="font-mono-tabular rounded border border-[#2E3440] bg-[#121417] px-2 py-0.5 text-[11px] text-[#EDF0F5]">
              {media?.fileName} • {mediaSummary}
            </span>
          )}
        </div>
      </header>

      {errorMessage && (
        <div className="flex items-center justify-between border-b border-red-900/60 bg-red-950/50 px-3 py-1.5 text-xs text-red-200">
          <span>{errorMessage}</span>
          <button
            type="button"
            onClick={() => setErrorMessage(null)}
            className="ml-4 text-red-300 underline hover:text-white"
          >
            Fermer
          </button>
        </div>
      )}

      {/* 2. Video Player Zone */}
      <div className="relative flex min-h-[260px] flex-1 items-center justify-center overflow-hidden border-b border-[#242832] bg-[#090A0C]">
        {streamUrl ? (
          <video
            ref={videoRef}
            src={streamUrl}
            onClick={togglePlayPause}
            onPlay={() => setIsPlaying(true)}
            onPause={() => setIsPlaying(false)}
            onEnded={() => setIsPlaying(false)}
            onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
            className="max-h-[48vh] w-full cursor-pointer object-contain"
          />
        ) : (
          <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
            {isLoadingMedia ? (
              <>
                <Loader2 className="h-8 w-8 animate-spin text-[#3B82F6]" />
                <p className="text-sm text-[#9AA3B2]">Analyse FFprobe et préparation de la vidéo…</p>
              </>
            ) : (
              <>
                <p className="text-sm text-[#9AA3B2]">
                  Déposez une vidéo ici, cliquez sur <strong className="text-[#EDF0F5]">Ouvrir</strong> ou testez avec la vidéo de démonstration.
                </p>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={() => fileInputRef.current?.click()}
                    className="inline-flex items-center gap-1.5 rounded border border-[#2E3440] bg-[#1A1D23] px-3 py-1.5 text-xs font-medium hover:bg-[#22262E]"
                  >
                    <FolderOpen className="h-3.5 w-3.5" />
                    Choisir un fichier vidéo
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleLoadDemo()}
                    className="inline-flex items-center gap-1.5 rounded border border-[#3B82F6]/60 bg-[#1E3A5F]/50 px-3 py-1.5 text-xs font-medium text-[#EDF0F5] hover:bg-[#1E3A5F]"
                  >
                    <Film className="h-3.5 w-3.5 text-[#60A5FA]" />
                    Charger la vidéo démo (24s)
                  </button>
                </div>
              </>
            )}
          </div>
        )}

        {isDraggingFile && (
          <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center border-2 border-dashed border-[#3B82F6] bg-[#121417]/80">
            <span className="rounded bg-[#1A1D23] px-4 py-2 text-sm font-medium text-[#EDF0F5] shadow">
              Relâchez pour ouvrir la vidéo
            </span>
          </div>
        )}
      </div>

      {/* 3. Playback Controls & SeekBar */}
      <section className="border-b border-[#2E3440] bg-[#1A1D23] px-3 py-2">
        <div className="mb-1.5 flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              onClick={() => stepFrame(-1)}
              disabled={!media}
              title="Image précédente (Flèche gauche)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <ChevronLeft className="h-3.5 w-3.5" />
              Image
            </button>

            <button
              type="button"
              onClick={togglePlayPause}
              disabled={!media}
              title="Lecture / Pause (Espace)"
              className="inline-flex min-w-[88px] items-center justify-center gap-1.5 rounded border border-[#2E3440] bg-[#22262E] px-2.5 py-1 text-xs font-medium hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              {isPlaying ? <Pause className="h-3.5 w-3.5" /> : <Play className="h-3.5 w-3.5" />}
              {isPlaying ? 'Pause' : 'Lecture'}
            </button>

            <button
              type="button"
              onClick={() => stepFrame(1)}
              disabled={!media}
              title="Image suivante (Flèche droite)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              Image
              <ChevronRight className="h-3.5 w-3.5" />
            </button>

            <span className="font-mono-tabular ml-3 text-xs text-[#EDF0F5]">
              {formatTimecode(currentTime, true)} <span className="text-[#666E7D]">/</span>{' '}
              {formatTimecode(duration)}
            </span>
          </div>

          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => setIsMuted((m) => !m)}
              disabled={!media}
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
              aria-label={isMuted ? 'Activer le son' : 'Couper le son'}
            >
              {isMuted || volume === 0 ? (
                <VolumeX className="h-3.5 w-3.5 text-[#9AA3B2]" />
              ) : (
                <Volume2 className="h-3.5 w-3.5 text-[#EDF0F5]" />
              )}
            </button>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={isMuted ? 0 : volume}
              disabled={!media}
              onChange={(e) => {
                const v = Number(e.target.value);
                setVolume(v);
                if (v > 0 && isMuted) setIsMuted(false);
              }}
              className="w-20 cursor-pointer accent-[#3B82F6] disabled:opacity-40"
              aria-label="Volume"
            />

            <label className="flex items-center gap-1.5 text-xs text-[#9AA3B2]">
              <span>Vitesse :</span>
              <select
                value={playbackRate}
                disabled={!media}
                onChange={(e) => setPlaybackRate(Number(e.target.value))}
                className="rounded border border-[#2E3440] bg-[#22262E] px-1.5 py-1 text-xs text-[#EDF0F5] focus:border-[#3B82F6] focus:outline-none disabled:opacity-40"
              >
                <option value={0.5}>0.5x</option>
                <option value={1}>1x</option>
                <option value={1.5}>1.5x</option>
                <option value={2}>2x</option>
              </select>
            </label>
          </div>
        </div>

        <SeekBar
          currentTime={currentTime}
          duration={duration}
          disabled={!media}
          spriteStatus={spriteStatus}
          onSeek={seekTo}
        />
      </section>

      {/* 4. Timeline Editing Tools + Timeline */}
      <section className="border-b border-[#2E3440] bg-[#121417] px-3 py-2">
        <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <button
              type="button"
              onClick={handleSplit}
              disabled={!media}
              title="Couper au playhead (S)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2.5 py-1 text-xs font-medium hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <Scissors className="h-3.5 w-3.5 text-[#F59E0B]" />
              Split (S)
            </button>

            <button
              type="button"
              onClick={handleSetIn}
              disabled={!media}
              title="Définir le début au playhead (I)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <ArrowLeftToLine className="h-3.5 w-3.5" />
              In (I)
            </button>

            <button
              type="button"
              onClick={handleSetOut}
              disabled={!media}
              title="Définir la fin au playhead (O)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <ArrowRightToLine className="h-3.5 w-3.5" />
              Out (O)
            </button>

            <button
              type="button"
              onClick={handleRemoveMarker}
              disabled={!media || segments.length <= 1}
              title="Supprimer un marqueur (fusionner les segments)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <Trash2 className="h-3.5 w-3.5" />
              Supprimer coupe
            </button>

            <div className="mx-1 h-4 w-px bg-[#2E3440]" />

            <button
              type="button"
              onClick={handleUndo}
              disabled={!canUndo}
              title="Annuler (Ctrl+Z)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <Undo2 className="h-3.5 w-3.5" />
              Annuler
            </button>

            <button
              type="button"
              onClick={handleRedo}
              disabled={!canRedo}
              title="Rétablir (Ctrl+Y)"
              className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-xs hover:bg-[#2A2F3A] disabled:opacity-40"
            >
              <Redo2 className="h-3.5 w-3.5" />
              Rétablir
            </button>
          </div>

          <div className="flex items-center gap-2">
            <label className="inline-flex cursor-pointer items-center gap-1.5 rounded border border-[#2E3440] bg-[#1A1D23] px-2 py-1 text-xs text-[#9AA3B2]">
              <input
                type="checkbox"
                checked={snapToKeyframes}
                onChange={(e) => setSnapToKeyframes(e.target.checked)}
                className="accent-[#F59E0B]"
              />
              <Magnet className="h-3.5 w-3.5 text-[#F59E0B]" />
              <span>Aimanter aux keyframes</span>
            </label>

            <div className="flex items-center gap-1">
              <button
                type="button"
                onClick={() => setZoom((z) => Math.max(1, Number((z / 1.5).toFixed(2))))}
                disabled={!media || zoom <= 1}
                title="Zoom arrière"
                className="rounded border border-[#2E3440] bg-[#22262E] p-1 hover:bg-[#2A2F3A] disabled:opacity-40"
              >
                <ZoomOut className="h-3.5 w-3.5" />
              </button>
              <span className="font-mono-tabular w-11 text-center text-[11px] text-[#9AA3B2]">
                {zoom.toFixed(1)}x
              </span>
              <button
                type="button"
                onClick={() => setZoom((z) => Math.min(16, Number((z * 1.5).toFixed(2))))}
                disabled={!media || zoom >= 16}
                title="Zoom avant"
                className="rounded border border-[#2E3440] bg-[#22262E] p-1 hover:bg-[#2A2F3A] disabled:opacity-40"
              >
                <ZoomIn className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                onClick={() => setZoom(1)}
                disabled={!media || zoom === 1}
                title="Ajuster à la fenêtre"
                className="inline-flex items-center gap-1 rounded border border-[#2E3440] bg-[#22262E] px-2 py-1 text-[11px] hover:bg-[#2A2F3A] disabled:opacity-40"
              >
                <Maximize2 className="h-3 w-3" />
                Ajuster
              </button>
            </div>
          </div>
        </div>

        <Timeline
          duration={duration}
          currentTime={currentTime}
          segments={segments}
          selectedSegmentId={selectedSegmentId}
          keyframes={media?.keyframes ?? []}
          snapToKeyframes={snapToKeyframes}
          spriteStatus={spriteStatus}
          zoom={zoom}
          onZoomChange={setZoom}
          onSeek={seekTo}
          onSelectSegment={handleSelectSegment}
          onMoveMarker={handleMoveMarker}
        />
      </section>

      {/* 5. Bottom Panels: Segments, Export Options, Progress */}
      <SegmentsPanel
        segments={segments}
        selectedSegmentId={selectedSegmentId}
        exportMode={exportMode}
        crf={crf}
        template={template}
        exportStatus={exportStatus}
        isExporting={isExporting}
        onSelectSegment={handleSelectSegment}
        onToggleSegment={handleToggleSegment}
        onRenameSegment={handleRenameSegment}
        onChangeCrf={setCrf}
        onChangeTemplate={setTemplate}
        onCancelExport={() => void handleCancelExport()}
      />
    </div>
  );
}
