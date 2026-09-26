param([string]$Destination = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$lock = Get-Content (Join-Path $PSScriptRoot 'tools.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $PSScriptRoot 'cache'
New-Item -ItemType Directory -Force -Path $Destination,$cache | Out-Null
foreach ($tool in $lock.tools) {
 $archive = Join-Path $cache ([IO.Path]::GetFileName($tool.url))
 if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $tool.url -OutFile $archive }
 if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $tool.sha256) { throw "Hash mismatch: $($tool.name)" }
 if ($archive.EndsWith('.zip')) {
  $expanded = Join-Path $cache ($tool.name + '-' + $tool.version)
  Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
  foreach ($name in $(if ($tool.name -eq 'ffmpeg') { @('ffmpeg.exe','ffprobe.exe') } else { @($tool.name + '.exe') })) {
   $file = Get-ChildItem -LiteralPath $expanded -Recurse -File -Filter $name | Select-Object -First 1
   if (!$file) { throw "Missing $name" }
   Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $name) -Force
  }
  $noticeDir = Join-Path $Destination ('licenses/' + $tool.name)
  New-Item -ItemType Directory -Force -Path $noticeDir | Out-Null
  Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object { $_.Name -match 'LICENSE|COPYING|NOTICE|README' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $noticeDir -Force }
 } else { Copy-Item -LiteralPath $archive -Destination (Join-Path $Destination 'yt-dlp.exe') -Force }
 Write-Host "Verified $($tool.name) $($tool.version)"
}
$files = @('ffmpeg.exe','ffprobe.exe','yt-dlp.exe','deno.exe','N_m3u8DL-RE.exe')
$hashes = @{}
foreach ($file in $files) { $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $Destination $file)).Hash.ToLowerInvariant() }
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'binary-hashes.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools.lock.json') -Destination $Destination -Force
