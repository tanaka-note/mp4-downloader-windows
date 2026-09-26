param([string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'), [switch]$ReleaseSmoke)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$diagnostics = Join-Path $root ('artifacts/self-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $diagnostics | Out-Null
$executable = Join-Path ([IO.Path]::GetFullPath($PublishDirectory)) 'Mp4Downloader.App.exe'
$arguments = @('--self-test', ('"' + $diagnostics + '"'))
if ($ReleaseSmoke) { $arguments += '--release-smoke' }
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(55000)) { throw 'App diagnostic did not complete within its deadline' }
$report = Join-Path $diagnostics 'self-test.json'
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw 'Published application startup failed' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (!$result.Startup -or !$result.Media -or (!$result.WebView2DynamicDiscovery -and !($ReleaseSmoke -and $result.BrowserSkippedElevated)) -or !$result.MissingRuntimeHandled -or !$result.SafeSave -or !$result.TempCleanup) { throw 'Published app/browser diagnostic failed' }
if ($result.BrowserSkippedElevated) { Write-Host 'Browser playback NOT tested: hosted runner is elevated; Runtime environment creation and missing-Runtime failure tested.' }
Write-Host "Published startup, dynamic WebView2 discovery, safe-save and cleanup passed. Defender: $($result.Defender); pipeline: $($result.PipelineOutcome); browser: $($result.Runtime)."
