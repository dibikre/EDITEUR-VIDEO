import express from 'express';
import type { Request, Response } from 'express';
import multer from 'multer';
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { createServer as createViteServer } from 'vite';

interface MediaInfo {
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

interface SpriteStatus {
  intervalSeconds: number;
  thumbWidth: number;
  thumbHeight: number;
  columns: number;
  rows: number;
  count: number;
  readySheets: string[];
  done: boolean;
  progress: number;
  error: string | null;
}

interface VideoSession {
  id: string;
  fileName: string;
  filePath: string;
  ext: string;
  mediaInfo: MediaInfo;
  spriteDir: string;
  spriteStatus: SpriteStatus;
}

interface ExportSegmentInput {
  id: string;
  name: string;
  start: number;
  end: number;
  index: number;
}

interface SegmentExportState {
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

interface ExportJobState {
  jobId: string;
  videoId: string;
  mode: 'fast' | 'precise';
  cancelled: boolean;
  done: boolean;
  segments: SegmentExportState[];
  activeProcesses: Set<ChildProcess>;
  outputDir: string;
}

const PORT = 3000;
const BASE_TMP = '/tmp/videosplitter';
const UPLOAD_DIR = path.join(BASE_TMP, 'uploads');
const CACHE_DIR = path.join(BASE_TMP, 'cache');
const EXPORT_DIR = path.join(BASE_TMP, 'exports');

fs.mkdirSync(UPLOAD_DIR, { recursive: true });
fs.mkdirSync(CACHE_DIR, { recursive: true });
fs.mkdirSync(EXPORT_DIR, { recursive: true });

const sessions = new Map<string, VideoSession>();
const exportJobs = new Map<string, ExportJobState>();

const upload = multer({
  dest: UPLOAD_DIR,
  limits: { fileSize: 2 * 1024 * 1024 * 1024 },
});

function sanitizeFileName(name: string): string {
  return name.replace(/[^a-zA-Z0-9._-]/g, '_');
}

function runCommandCapture(bin: string, args: string[]): Promise<{ code: number; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(bin, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8');
    });
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });
    child.on('error', reject);
    child.on('close', (code) => {
      resolve({ code: code ?? 1, stdout, stderr });
    });
  });
}

function parseFrameRate(raw: string | undefined): number {
  if (!raw) return 30;
  const parts = raw.split('/');
  if (parts.length === 2) {
    const num = Number(parts[0]);
    const den = Number(parts[1]);
    if (den > 0 && Number.isFinite(num)) {
      return Math.round((num / den) * 1000) / 1000;
    }
  }
  const val = Number(raw);
  return Number.isFinite(val) && val > 0 ? val : 30;
}

async function probeMedia(filePath: string, id: string, fileName: string): Promise<MediaInfo> {
  const probeRes = await runCommandCapture('ffprobe', [
    '-v', 'error',
    '-show_format',
    '-show_streams',
    '-of', 'json',
    filePath,
  ]);

  if (probeRes.code !== 0) {
    throw new Error(`Impossible d'analyser la vidéo : ${probeRes.stderr.trim() || 'format invalide'}`);
  }

  const parsed = JSON.parse(probeRes.stdout) as {
    format?: { duration?: string };
    streams?: Array<{
      codec_type?: string;
      codec_name?: string;
      width?: number;
      height?: number;
      avg_frame_rate?: string;
      r_frame_rate?: string;
      duration?: string;
    }>;
  };

  const videoStream = parsed.streams?.find((s) => s.codec_type === 'video');
  if (!videoStream) {
    throw new Error('Aucun flux vidéo détecté dans ce fichier.');
  }
  const audioStream = parsed.streams?.find((s) => s.codec_type === 'audio');

  const duration = Number(parsed.format?.duration ?? videoStream.duration ?? 0);
  if (!Number.isFinite(duration) || duration <= 0) {
    throw new Error('Durée vidéo invalide ou nulle.');
  }

  const fps = parseFrameRate(videoStream.avg_frame_rate !== '0/0' ? videoStream.avg_frame_rate : videoStream.r_frame_rate);

  // Extract keyframes
  const kfRes = await runCommandCapture('ffprobe', [
    '-v', 'error',
    '-select_streams', 'v:0',
    '-show_entries', 'packet=pts_time,flags',
    '-of', 'csv=p=0',
    filePath,
  ]);

  const keyframes: number[] = [];
  if (kfRes.code === 0) {
    const lines = kfRes.stdout.split(/\r?\n/);
    for (const line of lines) {
      if (!line) continue;
      const [ptsStr, flags] = line.split(',');
      if (flags && flags.includes('K')) {
        const t = Number(ptsStr);
        if (Number.isFinite(t) && t >= 0) {
          keyframes.push(t);
        }
      }
    }
  }
  if (keyframes.length === 0) {
    keyframes.push(0);
  }
  keyframes.sort((a, b) => a - b);

  return {
    id,
    fileName,
    duration,
    width: videoStream.width ?? 1280,
    height: videoStream.height ?? 720,
    fps,
    videoCodec: videoStream.codec_name ?? 'unknown',
    audioCodec: audioStream?.codec_name ?? null,
    hasAudio: Boolean(audioStream),
    keyframes,
  };
}

function startSpriteGeneration(session: VideoSession): void {
  const { duration, width, height } = session.mediaInfo;
  const thumbWidth = 160;
  const rawHeight = Math.round((height / Math.max(1, width)) * thumbWidth);
  const thumbHeight = Math.max(2, rawHeight % 2 === 0 ? rawHeight : rawHeight + 1);
  const intervalSeconds = Math.max(1, Math.ceil(duration / 600));
  const columns = 10;
  const rows = 10;
  const expectedCount = Math.max(1, Math.ceil(duration / intervalSeconds));

  session.spriteStatus = {
    intervalSeconds,
    thumbWidth,
    thumbHeight,
    columns,
    rows,
    count: expectedCount,
    readySheets: [],
    done: false,
    progress: 0,
    error: null,
  };

  fs.mkdirSync(session.spriteDir, { recursive: true });

  const args: string[] = ['-hide_banner', '-y'];
  if (duration > 600) {
    args.push('-skip_frame', 'nokey');
  }
  args.push(
    '-i', session.filePath,
    '-an', '-sn', '-dn',
    '-vf', `fps=1/${intervalSeconds},scale=${thumbWidth}:-2,tile=${columns}x${rows}`,
    '-q:v', '5',
    '-progress', 'pipe:1',
    '-nostats',
    path.join(session.spriteDir, 'sprite_%03d.jpg'),
  );

  const child = spawn('ffmpeg', args, { stdio: ['ignore', 'pipe', 'pipe'] });

  const scanSheets = () => {
    try {
      const files = fs
        .readdirSync(session.spriteDir)
        .filter((f) => /^sprite_\d{3}\.jpg$/.test(f))
        .sort();
      session.spriteStatus.readySheets = files;
    } catch {
      // Ignore transient read errors during generation
    }
  };

  const pollTimer = setInterval(scanSheets, 350);

  let stderrBuf = '';
  child.stdout.on('data', (chunk: Buffer) => {
    const text = chunk.toString('utf8');
    for (const line of text.split(/\r?\n/)) {
      if (line.startsWith('out_time_us=')) {
        const us = Number(line.slice('out_time_us='.length));
        if (Number.isFinite(us) && duration > 0) {
          session.spriteStatus.progress = Math.min(0.99, us / (duration * 1_000_000));
        }
      }
    }
  });

  child.stderr.on('data', (chunk: Buffer) => {
    stderrBuf += chunk.toString('utf8');
  });

  child.on('close', (code) => {
    clearInterval(pollTimer);
    scanSheets();
    session.spriteStatus.done = true;
    session.spriteStatus.progress = 1;
    if (code !== 0 && session.spriteStatus.readySheets.length === 0) {
      session.spriteStatus.error = stderrBuf.slice(-400) || 'Erreur FFmpeg lors de la génération des vignettes.';
    }
  });
}

async function registerVideoFile(filePath: string, originalName: string): Promise<VideoSession> {
  const id = crypto.randomBytes(8).toString('hex');
  const ext = path.extname(originalName) || '.mp4';
  const mediaInfo = await probeMedia(filePath, id, originalName);
  const spriteDir = path.join(CACHE_DIR, id);

  const session: VideoSession = {
    id,
    fileName: originalName,
    filePath,
    ext,
    mediaInfo,
    spriteDir,
    spriteStatus: {
      intervalSeconds: 1,
      thumbWidth: 160,
      thumbHeight: 90,
      columns: 10,
      rows: 10,
      count: 1,
      readySheets: [],
      done: false,
      progress: 0,
      error: null,
    },
  };

  sessions.set(id, session);
  startSpriteGeneration(session);
  return session;
}

function formatSegmentFileName(template: string, baseName: string, index: number, ext: string): string {
  const nn = String(index).padStart(2, '0');
  const raw = template
    .replaceAll('{nom}', baseName)
    .replaceAll('{NN}', nn)
    .replaceAll('{N}', String(index));
  return `${sanitizeFileName(raw)}${ext}`;
}

function runSingleExport(
  job: ExportJobState,
  session: VideoSession,
  seg: ExportSegmentInput,
  segState: SegmentExportState,
  crf: number,
): Promise<void> {
  return new Promise((resolve) => {
    if (job.cancelled) {
      segState.status = 'cancelled';
      resolve();
      return;
    }

    const duration = Math.max(0.1, seg.end - seg.start);
    const outPath = path.join(job.outputDir, segState.fileName);
    const partPath = `${outPath}.part`;

    // Check keyframe alignment in fast mode
    if (job.mode === 'fast') {
      const kfs = session.mediaInfo.keyframes;
      const nearest = kfs.reduce((best, k) => (Math.abs(k - seg.start) < Math.abs(best - seg.start) ? k : best), kfs[0] ?? 0);
      if (Math.abs(nearest - seg.start) > 0.15) {
        segState.warning = `Coupe à ${seg.start.toFixed(2)}s hors keyframe (proche : ${nearest.toFixed(2)}s). Préférez le mode Précis pour une coupe à l'image.`;
      }
    }

    const args: string[] = [
      '-hide_banner',
      '-y',
      '-ss', seg.start.toFixed(3),
      '-i', session.filePath,
      '-t', duration.toFixed(3),
    ];

    if (job.mode === 'fast') {
      args.push('-c', 'copy', '-map', '0', '-avoid_negative_ts', 'make_zero');
    } else {
      args.push(
        '-c:v', 'libx264',
        '-preset', 'veryfast',
        '-crf', String(crf),
        '-c:a', 'aac',
        '-b:a', '192k',
        '-map', '0:v:0',
        '-map', '0:a?',
      );
    }

    const outExt = job.mode === 'precise' ? '.mp4' : session.ext;
    const format = outExt.replace('.', '').toLowerCase() || 'mp4';
    args.push('-f', format === 'mkv' ? 'matroska' : format, '-progress', 'pipe:1', '-nostats', partPath);

    segState.status = 'running';
    const startedAt = Date.now();
    const child = spawn('ffmpeg', args, { stdio: ['ignore', 'pipe', 'pipe'] });
    job.activeProcesses.add(child);

    let stderrBuf = '';
    child.stdout.on('data', (chunk: Buffer) => {
      const text = chunk.toString('utf8');
      for (const line of text.split(/\r?\n/)) {
        if (line.startsWith('out_time_us=')) {
          const us = Number(line.slice('out_time_us='.length));
          if (Number.isFinite(us) && duration > 0) {
            segState.fraction = Math.min(0.99, us / (duration * 1_000_000));
            segState.elapsedMs = Date.now() - startedAt;
          }
        }
      }
    });

    child.stderr.on('data', (chunk: Buffer) => {
      stderrBuf += chunk.toString('utf8');
    });

    child.on('close', (code) => {
      job.activeProcesses.delete(child);
      segState.elapsedMs = Date.now() - startedAt;

      if (job.cancelled) {
        fs.rmSync(partPath, { force: true });
        segState.status = 'cancelled';
        resolve();
        return;
      }

      if (code === 0 && fs.existsSync(partPath)) {
        fs.renameSync(partPath, outPath);
        segState.fraction = 1;
        segState.status = 'completed';
        segState.downloadUrl = `/api/export/${job.jobId}/download/${encodeURIComponent(segState.fileName)}`;
      } else {
        fs.rmSync(partPath, { force: true });
        segState.status = 'error';
        segState.error = stderrBuf.slice(-300) || `FFmpeg a quitté avec le code ${code}.`;
      }
      resolve();
    });
  });
}

async function processExportQueue(
  job: ExportJobState,
  session: VideoSession,
  segments: ExportSegmentInput[],
  crf: number,
): Promise<void> {
  // Max 2 concurrent FFmpeg exports (SPEC §3 rule 8)
  const queue = segments.map((seg, i) => ({ seg, state: job.segments[i] }));
  let cursor = 0;

  const worker = async () => {
    while (cursor < queue.length && !job.cancelled) {
      const idx = cursor++;
      const item = queue[idx];
      if (!item) break;
      await runSingleExport(job, session, item.seg, item.state, crf);
    }
  };

  await Promise.all([worker(), worker()]);
  if (job.cancelled) {
    for (const s of job.segments) {
      if (s.status === 'pending' || s.status === 'running') {
        s.status = 'cancelled';
      }
    }
  }
  job.done = true;
}

async function startServer(): Promise<void> {
  const app = express();
  app.use(express.json());

  app.get('/api/health', (_req: Request, res: Response) => {
    res.json({ ok: true });
  });

  app.post('/api/videos/upload', upload.single('video'), async (req: Request, res: Response) => {
    try {
      if (!req.file) {
        res.status(400).json({ error: 'Aucun fichier vidéo reçu.' });
        return;
      }
      const ext = path.extname(req.file.originalname) || '.mp4';
      const finalPath = `${req.file.path}${ext}`;
      fs.renameSync(req.file.path, finalPath);

      const session = await registerVideoFile(finalPath, req.file.originalname);
      res.json({
        media: session.mediaInfo,
        streamUrl: `/api/videos/${session.id}/stream`,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Erreur lors du chargement vidéo.';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/videos/demo', async (_req: Request, res: Response) => {
    try {
      const demoPath = path.join(UPLOAD_DIR, `demo_${Date.now()}.mp4`);
      // Generate a crisp 24s multi-scene MP4 with moving bars, timecode overlay, and audio tone
      const gen = await runCommandCapture('ffmpeg', [
        '-hide_banner',
        '-y',
        '-f', 'lavfi',
        '-i', 'testsrc2=size=1280x720:rate=30:duration=24',
        '-f', 'lavfi',
        '-i', 'sine=frequency=440:beep_factor=4:duration=24',
        '-c:v', 'libx264',
        '-preset', 'ultrafast',
        '-pix_fmt', 'yuv420p',
        '-g', '60',
        '-c:a', 'aac',
        '-b:a', '128k',
        demoPath,
      ]);

      if (gen.code !== 0) {
        throw new Error(`Échec de génération de la vidéo de démonstration : ${gen.stderr.slice(-250)}`);
      }

      const session = await registerVideoFile(demoPath, 'demo_videosplitter_720p.mp4');
      res.json({
        media: session.mediaInfo,
        streamUrl: `/api/videos/${session.id}/stream`,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Erreur de génération démo.';
      res.status(500).json({ error: message });
    }
  });

  app.get('/api/videos/:id/stream', (req: Request, res: Response) => {
    const id = String(req.params.id);
    const session = sessions.get(id);
    if (!session || !fs.existsSync(session.filePath)) {
      res.status(404).json({ error: 'Vidéo introuvable.' });
      return;
    }

    const stat = fs.statSync(session.filePath);
    const fileSize = stat.size;
    const range = req.headers.range;
    const mime = session.ext === '.webm' ? 'video/webm' : session.ext === '.mov' ? 'video/quicktime' : 'video/mp4';

    if (range) {
      const parts = range.replace(/bytes=/, '').split('-');
      const start = parseInt(parts[0] ?? '0', 10);
      const end = parts[1] ? parseInt(parts[1], 10) : fileSize - 1;
      const chunkSize = end - start + 1;
      const stream = fs.createReadStream(session.filePath, { start, end });
      res.writeHead(206, {
        'Content-Range': `bytes ${start}-${end}/${fileSize}`,
        'Accept-Ranges': 'bytes',
        'Content-Length': chunkSize,
        'Content-Type': mime,
      });
      stream.pipe(res);
    } else {
      res.writeHead(200, {
        'Content-Length': fileSize,
        'Content-Type': mime,
        'Accept-Ranges': 'bytes',
      });
      fs.createReadStream(session.filePath).pipe(res);
    }
  });

  app.get('/api/videos/:id/sprites', (req: Request, res: Response) => {
    const id = String(req.params.id);
    const session = sessions.get(id);
    if (!session) {
      res.status(404).json({ error: 'Session introuvable.' });
      return;
    }
    res.json({
      ...session.spriteStatus,
      sheetUrls: session.spriteStatus.readySheets.map((f) => `/api/videos/${session.id}/sprites/${f}`),
    });
  });

  app.get('/api/videos/:id/sprites/:sheet', (req: Request, res: Response) => {
    const id = String(req.params.id);
    const sheet = String(req.params.sheet);
    const session = sessions.get(id);
    if (!session || !/^sprite_\d{3}\.jpg$/.test(sheet)) {
      res.status(404).end();
      return;
    }
    const fullPath = path.join(session.spriteDir, sheet);
    if (!fs.existsSync(fullPath)) {
      res.status(404).end();
      return;
    }
    res.setHeader('Cache-Control', 'public, max-age=3600');
    res.sendFile(fullPath);
  });

  app.post('/api/export', (req: Request, res: Response) => {
    const body = req.body as {
      videoId?: string;
      mode?: 'fast' | 'precise';
      crf?: number;
      template?: string;
      segments?: ExportSegmentInput[];
    };

    const session = body.videoId ? sessions.get(body.videoId) : undefined;
    if (!session) {
      res.status(404).json({ error: 'Aucune vidéo active pour l\'export.' });
      return;
    }

    const segments = body.segments ?? [];
    if (segments.length === 0) {
      res.status(400).json({ error: 'Aucun segment activé à exporter.' });
      return;
    }

    const jobId = crypto.randomBytes(6).toString('hex');
    const outputDir = path.join(EXPORT_DIR, jobId);
    fs.mkdirSync(outputDir, { recursive: true });

    const baseName = path.basename(session.fileName, path.extname(session.fileName));
    const template = body.template?.trim() || '{nom}_part{NN}';
    const mode = body.mode === 'precise' ? 'precise' : 'fast';
    const crf = Math.min(35, Math.max(12, Number(body.crf ?? 18)));

    const segStates: SegmentExportState[] = segments.map((seg) => ({
      segmentId: seg.id,
      name: seg.name,
      fileName: formatSegmentFileName(template, baseName, seg.index, mode === 'precise' ? '.mp4' : session.ext),
      status: 'pending',
      fraction: 0,
      elapsedMs: 0,
      warning: null,
      error: null,
      downloadUrl: null,
    }));

    const job: ExportJobState = {
      jobId,
      videoId: session.id,
      mode,
      cancelled: false,
      done: false,
      segments: segStates,
      activeProcesses: new Set(),
      outputDir,
    };

    exportJobs.set(jobId, job);
    void processExportQueue(job, session, segments, crf);

    res.json({ jobId });
  });

  app.get('/api/export/:jobId/status', (req: Request, res: Response) => {
    const job = exportJobs.get(String(req.params.jobId));
    if (!job) {
      res.status(404).json({ error: 'Tâche d\'export introuvable.' });
      return;
    }
    const totalFraction =
      job.segments.length > 0
        ? job.segments.reduce((acc, s) => acc + (s.status === 'completed' ? 1 : s.fraction), 0) / job.segments.length
        : 0;

    res.json({
      jobId: job.jobId,
      done: job.done,
      cancelled: job.cancelled,
      totalFraction,
      segments: job.segments,
    });
  });

  app.post('/api/export/:jobId/cancel', (req: Request, res: Response) => {
    const job = exportJobs.get(String(req.params.jobId));
    if (!job) {
      res.status(404).json({ error: 'Tâche introuvable.' });
      return;
    }
    job.cancelled = true;
    for (const proc of job.activeProcesses) {
      proc.kill('SIGKILL');
    }
    res.json({ ok: true });
  });

  app.get('/api/export/:jobId/download/:fileName', (req: Request, res: Response) => {
    const job = exportJobs.get(String(req.params.jobId));
    const fileName = sanitizeFileName(String(req.params.fileName));
    if (!job) {
      res.status(404).end();
      return;
    }
    const fullPath = path.join(job.outputDir, fileName);
    if (!fs.existsSync(fullPath)) {
      res.status(404).end();
      return;
    }
    res.download(fullPath, fileName);
  });

  const isProd = process.env.NODE_ENV === 'production';
  if (isProd) {
    const distDir = path.resolve('dist');
    app.use(express.static(distDir));
    app.get('/{*splat}', (_req: Request, res: Response) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  } else {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  }

  app.listen(PORT, '0.0.0.0', () => {
    console.log(`VideoSplitter server running on http://0.0.0.0:${PORT}`);
  });
}

void startServer();
