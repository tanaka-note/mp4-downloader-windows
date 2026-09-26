param([string]$SourceDirectory=(Join-Path $PSScriptRoot 'cache/n-source'),[string]$Output=(Join-Path $PSScriptRoot '../artifacts/n-built'))
$ErrorActionPreference='Stop'
$project=Join-Path $SourceDirectory 'src/N_m3u8DL-RE/N_m3u8DL-RE.csproj'
foreach($name in @('N_m3u8DL-RE','N_m3u8DL-RE.Common','N_m3u8DL-RE.Parser')){Copy-Item (Join-Path $PSScriptRoot ('streaming-locks/'+$name+'.packages.lock.json')) (Join-Path $SourceDirectory ('src/'+$name+'/packages.lock.json')) -Force}
dotnet publish $project -c Release -r win-x64 --self-contained true -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true -o $Output
if($LASTEXITCODE -ne 0){throw 'Streaming tool build failed'}
# No application source changes; lockfiles are supplied with the corresponding source package.
