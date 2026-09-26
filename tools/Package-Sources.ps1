param([string]$Output=(Join-Path $PSScriptRoot '../artifacts/Mp4Downloader-v0.1.0-third-party-sources.zip'))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest=Get-Content (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Raw | ConvertFrom-Json
$cache=Join-Path $PSScriptRoot 'cache/sources'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $Output){[IO.File]::Delete([IO.Path]::GetFullPath($Output))}
$zip=[IO.Compression.ZipFile]::Open([IO.Path]::GetFullPath($Output),[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($source in $manifest.sources){
  $path=Join-Path $cache $source.file
  if(!(Test-Path -LiteralPath $path)){Invoke-WebRequest -Uri $source.url -OutFile $path}
  if((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.sha256){throw ('Source checksum mismatch: '+$source.name)}
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$path,'sources/'+$source.file,[IO.Compression.CompressionLevel]::NoCompression) | Out-Null
 }
 foreach($relative in @(@($manifest.buildScripts)+@('tools/third-party-sources.lock.json','tools/media-sources.lock.json','tools/tools.lock.json','tools/binary-pins.json','docs/third-party-builds.md','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md','THIRD_PARTY_LICENSES.txt') | Sort-Object -Unique)){
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root $relative),$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
 }
 foreach($directory in @('licenses','tools/streaming-locks','tools/media-provenance')){
  foreach($file in Get-ChildItem (Join-Path $root $directory) -File -Recurse){
   $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
   [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
 }
} finally {$zip.Dispose()}
$hash=(Get-FileHash $Output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($Output+'.sha256',$hash+'  '+[IO.Path]::GetFileName($Output)+"`n")
Write-Host ('Corresponding source package created: '+$Output+'; SHA256: '+$hash)
