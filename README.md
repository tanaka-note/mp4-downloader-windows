# MP4 Downloader

Windows 11 x64 local-first, non-DRM VOD downloader. No application cloud service, telemetry, analytics or crash upload. Windows/WebView2/Defender may communicate according to OS settings.

Independent of T-ROOM, Downloader 1/2 and all existing services. See [v0.2 specification](docs/architecture.md).

Development: `pwsh ./tools/Install-Tools.ps1`, `dotnet test MP4Downloader.slnx -c Release`, then `pwsh ./tools/Publish.ps1`.
Use Windows PowerShell with `powershell -ExecutionPolicy Bypass -File` if PowerShell 7 is unavailable.
Run `artifacts/publish/Mp4Downloader.App.exe`. WebView2 Evergreen must be installed. Defender must return a verified clean result before a file can be saved.

This repository does not publish GitHub Releases or merge PRs automatically.
