param([string]$PublishDirectory=(Join-Path $PSScriptRoot '../artifacts/publish'), [switch]$ForRelease)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$published=[IO.Path]::GetFullPath($PublishDirectory)
$policy=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-policy.json') -Raw | ConvertFrom-Json
if($ForRelease -and !$policy.publicRedistributionApproved) { throw ('Public release blocked: ' + ($policy.blockers -join ' ')) }
foreach($file in @('Mp4Downloader.App.exe','Mp4Downloader.App.pri','Microsoft.UI.Xaml.dll','README.md','LICENSE','THIRD_PARTY_NOTICES.md','tools/ffmpeg.exe','tools/ffprobe.exe','tools/yt-dlp.exe','tools/deno.exe','tools/N_m3u8DL-RE.exe','tools/binary-hashes.json')) {
 if(!(Test-Path -LiteralPath (Join-Path $published $file))) { throw "Package file missing: $file" }
}
$commit=git -C $root rev-parse HEAD
if($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit' }
[IO.File]::WriteAllText((Join-Path $published 'BUILD-COMMIT.txt'),$commit.Trim())
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-policy.json') -Destination $published -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$name='Mp4Downloader-v'+$policy.version+'-win-x64'+$(if($ForRelease){''}else{'-INTERNAL-VALIDATION'})+'.zip'
$zip=Join-Path $root ('artifacts/'+$name)
if(Test-Path -LiteralPath $zip) { [IO.File]::Delete($zip) }
[IO.Compression.ZipFile]::CreateFromDirectory($published,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
 $entries=@{}
 foreach($entry in $archive.Entries) {
  if($entry.FullName.EndsWith('/')) {continue}
  if($entries.ContainsKey($entry.FullName)) {throw 'Duplicate archive entry'}
  $entries[$entry.FullName]=$true
  $original=Join-Path $published ($entry.FullName.Replace('/',[IO.Path]::DirectorySeparatorChar))
  $stream=$entry.Open()
  try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally {$stream.Dispose()}
  if($hash -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) {throw "Archive hash mismatch: $($entry.FullName)"}
 }
 if($entries.Count -ne @(Get-ChildItem -LiteralPath $published -File -Recurse).Count) {throw 'Archive file count mismatch'}
} finally {$archive.Dispose()}
$sha=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip+'.sha256',$sha+'  '+$name+"`n")
$unpacked=Join-Path $root 'artifacts/package-smoke'
if(Test-Path -LiteralPath $unpacked) {throw 'Package smoke directory already exists; use a fresh checkout/artifact root'}
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$unpacked)
Write-Host "ZIP bytes verified against publish: $zip; SHA256: $sha; public redistribution approved: $($policy.publicRedistributionApproved)"
