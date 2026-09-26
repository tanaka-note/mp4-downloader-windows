$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LicensePolicy.ps1')
$count=0
function MustReject([scriptblock]$Action,[string]$Expected){
 try{& $Action}catch{if($_.Exception.Message -notmatch $Expected){throw};$script:count++;return}
 throw 'License regression: unsafe fixture accepted'
}
$valid='--disable-autodetect --enable-gpl --enable-version3 --disable-network --enable-libopus --enable-libvpx --enable-libx264'
Assert-MediaLicenseConfiguration $valid
MustReject {Assert-MediaLicenseConfiguration ($valid+' --enable-nonfree')} 'Forbidden'
MustReject {Assert-MediaLicenseConfiguration ($valid+' --enable-libx265')} 'Unreviewed external'
MustReject {Assert-MediaLicenseConfiguration $valid.Replace('--enable-version3','')} 'Forbidden'
MustReject {Assert-MediaLicenseConfiguration ($valid+' --enable-autodetect')} 'Forbidden'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('mp4-license-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try{
 $pins=Get-Content (Join-Path $PSScriptRoot 'binary-pins.json') -Raw | ConvertFrom-Json -AsHashtable
 $key=@($pins.Keys)[0];$pins[$key]='0'*64;$badPins=Join-Path $temporary 'bad-pins.json'
 $pins | ConvertTo-Json | Set-Content $badPins
 MustReject {& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -BinaryPins $badPins} 'Binary checksum mismatch'
 MustReject {& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -NoticesDirectory $temporary} 'License notice missing'
 MustReject {& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -SourcePackage (Join-Path $temporary 'missing.zip')} 'Corresponding source archive missing'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $empty=Join-Path $temporary 'bad-source.zip';$archive=[IO.Compression.ZipFile]::Open($empty,[IO.Compression.ZipArchiveMode]::Create)
 $entry=$archive.CreateEntry('sources/ffmpeg.tar.gz');$stream=$entry.Open();$stream.WriteByte(1);$stream.Dispose();$archive.Dispose()
 MustReject {& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -SourcePackage $empty} 'Source archive checksum mismatch'
 MustReject {& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -RequireReleaseAssets -ReleaseAssetDirectory $temporary} 'Release asset missing'
}finally{Get-ChildItem -LiteralPath $temporary -File | ForEach-Object {[IO.File]::Delete($_.FullName)};[IO.Directory]::Delete($temporary)}
Write-Host ('License regression: '+$count+' negative cases rejected')
