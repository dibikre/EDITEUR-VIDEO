$dest = "src/VideoSplitter/Assets/ffmpeg"
$tmp  = "$env:TEMP\ffmpeg-extract"
$zip  = "$env:TEMP\ffmpeg-btbn.zip"
$url  = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"

New-Item -ItemType Directory -Force $dest | Out-Null
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null

curl.exe -L --fail -C - -o $zip $url
if ($LASTEXITCODE -ne 0) { throw "Telechargement echoue" }

tar -xf $zip -C $tmp
Copy-Item "$tmp\*\bin\ffmpeg.exe" $dest -Force
Copy-Item "$tmp\*\bin\ffprobe.exe" $dest -Force
Write-Host "OK : ffmpeg installe dans $dest"