export interface MediaInfo {
  id: string;
  fileName: string;
  duration: number;
  width: number;
  height: number;
  fps: number;
  videoCodec: string;
  audioCodec: string | null;
  hasAudio: boolean;
  keyframes: number[];
}

export interface SpriteStatus {
  intervalSeconds: number;
  thumbWidth: number;
  thumbHeight: number;
  columns: number;
  rows: number;
  count: number;
  readySheets: string[];
  sheetUrls: string[];
  done: boolean;
  progress: number;
  error: string | null;
}

export interface Segment {
  id: string;
  name: string;
  start: number;
  end: number;
  enabled: boolean;
}

export type ExportMode = 'fast' | 'precise';

export interface SegmentExportState {
  segmentId: string;
  name: string;
  fileName: string;
  status: 'pending' | 'running' | 'completed' | 'error' | 'cancelled';
  fraction: number;
  elapsedMs: number;
  warning: string | null;
  error: string | null;
  downloadUrl: string | null;
}

export interface ExportStatusResponse {
  jobId: string;
  done: boolean;
  cancelled: boolean;
  totalFraction: number;
  segments: SegmentExportState[];
}
