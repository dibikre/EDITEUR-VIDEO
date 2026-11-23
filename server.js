import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const port = Number(process.env.PORT || 3000);
const host = '0.0.0.0';

function listSourceFiles(dir, base = dir) {
  if (!fs.existsSync(dir)) return [];
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    if (entry.name === 'bin' || entry.name === 'obj') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...listSourceFiles(full, base));
    } else {
      files.push(path.relative(base, full).replaceAll('\\', '/'));
    }
  }
  return files.sort();
}

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ ok: true }));
    return;
  }

  const files = listSourceFiles(path.join(process.cwd(), 'src', 'VideoSplitter'));
  const fileItems = files.map((f) => `<li><code>src/VideoSplitter/${f}</code></li>`).join('\n');

  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html>
<html lang="fr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>VideoSplitter — Workspace WPF (.NET 10)</title>
  <style>
    body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: #121417; color: #EDF0F5; padding: 32px; }
    .card { max-width: 820px; margin: 0 auto; background: #1A1D23; border: 1px solid #2E3440; border-radius: 8px; padding: 24px; }
    h1 { margin: 0 0 8px; font-size: 22px; }
    p { color: #9AA3B2; line-height: 1.5; }
    pre { background: #090A0C; border: 1px solid #2E3440; border-radius: 6px; padding: 12px 14px; overflow-x: auto; color: #EDF0F5; }
    ul { columns: 2; gap: 24px; padding-left: 20px; color: #9AA3B2; }
    code { color: #60A5FA; }
  </style>
</head>
<body>
  <div class="card">
    <h1>VideoSplitter — Application Desktop WPF (.NET 10)</h1>
    <p>Ce dépôt contient le code source Windows WPF (C# / .NET 10 / LibVLCSharp / FFmpeg). L'exécution de l'interface graphique s'effectue en local sous Windows x64 :</p>
    <pre>dotnet build
dotnet run --project src\\VideoSplitter</pre>
    <p>Fichiers présents dans <code>src/VideoSplitter</code> (${files.length}) :</p>
    <ul>${fileItems}</ul>
  </div>
</body>
</html>`);
});

server.listen(port, host, () => {
  console.log(`VideoSplitter workspace preview listening on http://${host}:${port}`);
});
