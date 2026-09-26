param(
 [string]$ToolDirectory=(Join-Path $PSScriptRoot 'bin'),
 [string]$SourcePackage=(Join-Path $PSScriptRoot '../artifacts/Mp4Downloader-v0.1.0-third-party-sources.zip'),
 [string]$ToolLock=(Join-Path $PSScriptRoot 'tools.lock.json'),
 [string]$BinaryPins=(Join-Path $PSScriptRoot 'binary-pins.json'),
 [string]$NoticesDirectory=(Join-Path $PSScriptRoot '../licenses'),
 [string]$ReleaseAssetDirectory=(Join-Path $PSScriptRoot '../artifacts'),
 [switch]$RequireReleaseAssets
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock=Get-Content -LiteralPath $ToolLock -Raw | ConvertFrom-Json
foreach($item in $lock.tools){
 if(!$item.version -or $item.sha256 -notmatch '^[0-9a-f]{64}$' -or $item.url -match '/latest/'){throw 'Unpinned tool'}
 if($item.localArchive){
  if((Get-FileHash (Join-Path $PSScriptRoot $item.localArchive)).Hash.ToLowerInvariant() -ne $item.sha256){throw 'Tool archive checksum mismatch'}
 }
}
if((Test-Path (Join-Path $ToolDirectory 'yt-dlp.exe')) -or (Test-Path (Join-Path $ToolDirectory 'deno.exe'))){throw 'Obsolete standalone runtime bundled'}
$pins=Get-Content -LiteralPath $BinaryPins -Raw | ConvertFrom-Json -AsHashtable
foreach($name in @('ffmpeg.exe','ffprobe.exe','yt-dlp','python/python.exe','python/python314.dll','python/python314.zip','node.exe','N_m3u8DL-RE.exe')){if(!$pins.ContainsKey($name)){throw ('Required binary pin missing: '+$name)}}
foreach($file in Get-ChildItem $ToolDirectory -Recurse -File | Where-Object {$_.Extension -in '.exe','.dll','.pyd'}){
 $relative=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($ToolDirectory),$file.FullName).Replace('\','/')
 if(!$pins.ContainsKey($relative)){throw ('Unpinned tool runtime file: '+$relative)}
}
foreach($entry in $pins.GetEnumerator()){
 if($entry.Value -notmatch '^[0-9a-fA-F]{64}$'){throw 'Invalid binary pin'}
 if((Get-FileHash (Join-Path $ToolDirectory $entry.Key) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value){throw ('Binary checksum mismatch: '+$entry.Key)}
}
$ffVersion=& (Join-Path $ToolDirectory 'ffmpeg.exe') -version 2>&1 | Out-String
if($LASTEXITCODE -ne 0 -or $ffVersion -notmatch 'ffmpeg version 9\.0\.2(?:\s|-)'){throw 'FFmpeg version/source identity mismatch'}
$probeVersion=& (Join-Path $ToolDirectory 'ffprobe.exe') -version 2>&1 | Out-String
if($LASTEXITCODE -ne 0 -or $probeVersion -notmatch 'ffprobe version 9\.0\.2(?:\s|-)'){throw 'ffprobe version/source identity mismatch'}
$ytVersion=& (Join-Path $ToolDirectory 'python/python.exe') -I -B (Join-Path $ToolDirectory 'yt-dlp') --ignore-config --no-update --version
if($LASTEXITCODE -ne 0 -or $ytVersion.Trim() -ne '2026.08.19'){throw 'yt-dlp version mismatch'}
$pythonVersion=& (Join-Path $ToolDirectory 'python/python.exe') -I --version
if($LASTEXITCODE -ne 0 -or $pythonVersion.Trim() -ne 'Python 3.14.7'){throw 'Python version mismatch'}
$nodeVersion=& (Join-Path $ToolDirectory 'node.exe') --version
if($LASTEXITCODE -ne 0 -or $nodeVersion.Trim() -ne 'v24.21.0'){throw 'Node version mismatch'}
foreach($path in @('GPL-3.0.txt','GCC-Runtime-Library-Exception.txt','MinGW-w64-COPYING.txt','python-3.14.7-LICENSE.txt','node-v24.21.0-LICENSE.txt','yt-dlp-LICENSE.txt','yt-dlp-THIRD-PARTY-LICENSES.txt','N_m3u8DL-RE-LICENSE.txt','dotnet-runtime-10.0.12-LICENSE.TXT','dotnet-runtime-10.0.12-THIRD-PARTY-NOTICES.TXT','nuget/packages.json')){
 $notice=Join-Path $NoticesDirectory $path
 if(!(Test-Path $notice) -or (Get-Item $notice).Length -lt 20){throw ('License notice missing: '+$path)}
}
$configuration=& (Join-Path $ToolDirectory 'ffmpeg.exe') -hide_banner -buildconf 2>&1 | Out-String
if($LASTEXITCODE -ne 0){throw 'Cannot read FFmpeg build configuration'}
. (Join-Path $PSScriptRoot 'LicensePolicy.ps1')
Assert-MediaLicenseConfiguration $configuration
$provenance=Get-Content (Join-Path $PSScriptRoot 'media-provenance/media-sources.lock.json') -Raw | ConvertFrom-Json
if((Get-FileHash (Join-Path $PSScriptRoot 'media-provenance/Build-Media.sh')).Hash -ne (Get-FileHash (Join-Path $PSScriptRoot 'Build-Media.sh')).Hash){throw 'Media binary/build script provenance mismatch'}
$media=Get-Content (Join-Path $PSScriptRoot 'media-sources.lock.json') -Raw | ConvertFrom-Json
foreach($source in $media.sources){
 $original=@($provenance.sources | Where-Object name -eq $source.name)
 if($original.Count -ne 1 -or $original[0].sha256 -ne $source.sha256 -or $original[0].commit -ne $source.commit){throw 'Binary/source provenance mismatch'}
}
$manifest=Get-Content (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Raw | ConvertFrom-Json
foreach($name in @('ffmpeg','x264','libvpx','opus','yt-dlp','python','node','N_m3u8DL-RE','ejs','mingw')){if(@($manifest.sources | Where-Object name -eq $name).Count -ne 1){throw ('Required source manifest entry missing/duplicated: '+$name)}}
foreach($source in $media.sources){$preserved=@($manifest.sources | Where-Object name -eq $source.name);if($preserved.Count -ne 1 -or $preserved[0].sha256 -ne $source.sha256 -or $preserved[0].commit -ne $source.commit){throw 'Source package/media build mismatch'}}
if(!(Test-Path -LiteralPath $SourcePackage)){throw 'Corresponding source archive missing'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($SourcePackage))
try{
 $names=@{}
 foreach($entry in $zip.Entries){if($names.ContainsKey($entry.FullName)){throw 'Duplicate source archive entry'};$names[$entry.FullName]=$entry}
 foreach($source in $manifest.sources){
  if(!$source.commit -or !$source.license){throw 'Incomplete source manifest'}
  $entry=$names['sources/'+$source.file]
  if(!$entry){throw ('Required source missing: '+$source.name)}
  $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
  if($hash -ne $source.sha256){throw ('Source archive checksum mismatch: '+$source.name)}
 }
 foreach($relative in @($manifest.buildScripts)+@('tools/third-party-sources.lock.json','tools/media-sources.lock.json','tools/binary-pins.json','docs/third-party-builds.md','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md','THIRD_PARTY_LICENSES.txt')){
  $entry=$names[$relative];if(!$entry){throw ('Source build/notice entry missing: '+$relative)}
  $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
  if($hash -ne (Get-FileHash (Join-Path $root $relative)).Hash.ToLowerInvariant()){throw ('Source build/notice entry mismatch: '+$relative)}
 }
}finally{$zip.Dispose()}
if($RequireReleaseAssets){
 $required=@('Mp4Downloader-v0.1.0-win-x64.zip','Mp4Downloader-v0.1.0-third-party-sources.zip','SHA256SUMS','LICENSE','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md','THIRD_PARTY_LICENSES.txt')
 foreach($name in $required){if(!(Test-Path (Join-Path $ReleaseAssetDirectory $name))){throw ('Release asset missing: '+$name)}}
 $sums=@{}
 foreach($line in Get-Content (Join-Path $ReleaseAssetDirectory 'SHA256SUMS')){
  if($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+)$'){throw 'Invalid SHA256SUMS entry'}
  $name=$Matches[2];if($sums.ContainsKey($name)){throw 'Duplicate checksum entry'};$sums[$name]=$Matches[1]
  if((Get-FileHash (Join-Path $ReleaseAssetDirectory $name)).Hash.ToLowerInvariant() -ne $sums[$name]){throw ('Release asset checksum mismatch: '+$name)}
 }
 foreach($name in $required | Where-Object {$_ -ne 'SHA256SUMS'}){if(!$sums.ContainsKey($name)){throw ('Release checksum missing: '+$name)}}
}
Write-Host ('License gate passed: '+$pins.Count+' pinned runtime files; '+$manifest.sources.Count+' preserved source archives; no nonfree/unreviewed FFmpeg dependencies')
