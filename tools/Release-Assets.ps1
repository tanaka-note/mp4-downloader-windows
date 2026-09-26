$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory=Join-Path $root 'artifacts'
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md','THIRD_PARTY_LICENSES.txt')){Copy-Item (Join-Path $root $name) (Join-Path $directory $name) -Force}
$sums=foreach($name in @('Mp4Downloader-v0.1.0-win-x64.zip','Mp4Downloader-v0.1.0-third-party-sources.zip','LICENSE','THIRD_PARTY_NOTICES.md','THIRD_PARTY_TERMS.md','THIRD_PARTY_LICENSES.txt')){(Get-FileHash (Join-Path $directory $name)).Hash.ToLowerInvariant()+'  '+$name}
[IO.File]::WriteAllLines((Join-Path $directory 'SHA256SUMS'),$sums)
& (Join-Path $PSScriptRoot 'Test-LicenseGate.ps1') -ToolDirectory (Join-Path $directory 'package-smoke/tools') -RequireReleaseAssets
