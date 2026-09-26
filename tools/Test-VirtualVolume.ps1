$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vhd=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/cross-volume-test.vhd'))
if(!$vhd.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'VHD outside workspace'}
if(Test-Path -LiteralPath $vhd) {throw 'Test VHD already exists'}
if(Test-Path -LiteralPath 'Q:\') {throw 'Q drive is already in use; refuse to alter it'}
$create=Join-Path $root 'artifacts/create-test-volume.txt'
$detach=Join-Path $root 'artifacts/detach-test-volume.txt'
@"
create vdisk file="$vhd" maximum=128 type=expandable
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=ntfs quick label=MP4_TEST
assign letter=Q
"@ | Set-Content -LiteralPath $create
@"
select vdisk file="$vhd"
detach vdisk
"@ | Set-Content -LiteralPath $detach
try {
 $diskOutput=diskpart /s $create
 if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath 'Q:\')) {throw 'Isolated VHD creation failed'}
 New-Item -ItemType Directory -Path 'Q:\Mp4Validation' | Out-Null
 dotnet run --project (Join-Path $PSScriptRoot 'Mp4Downloader.Validation') -c Release --no-restore -- --cross-volume $root 'Q:\Mp4Validation'
 if($LASTEXITCODE -ne 0) {throw 'Distinct-volume copy validation failed'}
} finally {
 $detachOutput=diskpart /s $detach
 if($LASTEXITCODE -ne 0) {throw 'Test VHD detach failed; preserve VHD for recovery'}
 # Only this script's explicitly named, detached VHD and command files are removed.
 [IO.File]::Delete($vhd); [IO.File]::Delete($create); [IO.File]::Delete($detach)
}
