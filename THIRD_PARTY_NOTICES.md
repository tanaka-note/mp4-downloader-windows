# Third-party notices

Application source: GPL-3.0-or-later (LICENSE). No DRM decryption implementation is included.

| Component | Distribution license / origin |
|---|---|
| .NET runtime | MIT and bundled third-party notices; https://github.com/dotnet/runtime |
| Windows App SDK | Microsoft package terms and component notices; https://github.com/microsoft/WindowsAppSDK |
| WebView2 SDK/Runtime | Microsoft terms; Evergreen is separately installed by the user/OS |
| FFmpeg 9.0.2 Gyan essentials | GPLv3 build including libx264; https://www.gyan.dev/ffmpeg/builds/ |
| yt-dlp 2026.08.19 Windows executable | Bundled executable is GPLv3+; source project alone is Unlicense; https://github.com/yt-dlp/yt-dlp |
| Deno 2.9.7 | MIT and third-party dependencies; https://github.com/denoland/deno |
| N_m3u8DL-RE 0.6.0-beta | MIT and dependency terms; https://github.com/nilaoda/N_m3u8DL-RE |
| xUnit/test SDK | Development-only package notices, not shipped as application code |

Installer preserves notices found in downloaded archives under tools/bin/licenses. Versions, archive URLs and SHA256 are in tools/tools.lock.json. Local publish includes those notices, LICENSE, this document and the tool manifest.

Public binary redistribution is a separate release gate. A generic link to upstream or this notice alone is not a substitute for complete corresponding-source obligations. Assemble source, patches/build instructions and notices for the exact distributed FFmpeg/x264 and yt-dlp bundled dependencies, or provide an applicable compliant distribution mechanism, before a GitHub Release. Current local publish is for validation; no public binary Release is authorized in this task.

FFmpeg build source revision for the selected upstream release: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b . That revision alone does not include all third-party build dependencies. Do not remove this gate or claim compliance solely because the application uses separate processes.
