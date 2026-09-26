param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/publish'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Output = [IO.Path]::GetFullPath($Output)
dotnet publish (Join-Path $root 'src/Mp4Downloader.App/Mp4Downloader.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:RestoreLockedMode=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$toolDestination = Join-Path $Output 'tools'
New-Item -ItemType Directory -Force -Path $toolDestination | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bin') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $toolDestination -Recurse -Force }
foreach ($notice in @('LICENSE','THIRD_PARTY_NOTICES.md','README.md')) { Copy-Item -LiteralPath (Join-Path $root $notice) -Destination $Output -Force }
if (!(Test-Path -LiteralPath (Join-Path $Output 'Mp4Downloader.App.exe'))) { throw 'Executable missing' }
if (!(Test-Path -LiteralPath (Join-Path $Output 'Microsoft.UI.Xaml.dll'))) { throw 'Self-contained WinUI runtime missing' }
if (!(Test-Path -LiteralPath (Join-Path $Output 'Mp4Downloader.App.pri'))) { throw 'App XAML resource index missing' }
Write-Host 'Self-contained publish completed. This is not a public binary release.'
