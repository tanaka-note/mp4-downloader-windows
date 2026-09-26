# Third-party notices

Application source: MIT (`LICENSE`). Each separate tool and runtime retains its original license. `THIRD_PARTY_TERMS.md` passes through Microsoft's redistribution terms; no proprietary Microsoft component is relicensed under MIT or GPL. No DRM key/license-server operation is added by this application.

| Component | Distribution license / origin |
|---|---|
| .NET runtime | MIT and bundled third-party notices; https://github.com/dotnet/runtime |
| Windows App SDK | Microsoft package terms and component notices; https://github.com/microsoft/WindowsAppSDK |
| WebView2 SDK/Runtime | Microsoft terms; Evergreen is separately installed by the user/OS |
| FFmpeg/ffprobe 9.0.2 | Own fixed-source GPLv3+ build, commit 946fcce07b6dcd0331c8cc609192aeff5e1924f8; external libraries limited to x264, libvpx and Opus |
| x264 | GPLv2+, commit b35605ace3ddf7c1a5d67a2eb553f034aef41d55; combined FFmpeg executable GPLv3+ |
| libvpx 1.15.2 / Opus 1.5.2 | BSD-3-Clause, exact sources/hashes preserved |
| yt-dlp 2026.08.19 | Official zipimport: core/EJS Unlicense, Meriyah ISC, Astring MIT; NOT the PyInstaller Windows executable |
| CPython 3.14.7 | Official Windows embeddable distribution: PSF-2.0 plus complete bundled-library and Microsoft VC runtime terms |
| Node.js v24.21.0 | Official node.exe: MIT plus complete bundled-component LICENSE; npm not shipped |
| N_m3u8DL-RE 0.6.0-beta | Own MIT build, commit df70f0b3da0c630bd413bf617e758051f6b64757; pinned .NET/Spectre.Console/Ansi/System.CommandLine notices |
| GCC/MinGW runtime | GCC Runtime Library Exception / MinGW-w64 notices; Windows system DLLs not bundled |
| xUnit/test SDK | Development-only package notices, not shipped as application code |

Installer preserves notices found in downloaded archives under tools/bin/licenses. Versions, archive URLs and SHA256 are in tools/tools.lock.json. Local publish includes those notices, LICENSE, this document and the tool manifest.

The same GitHub Release provides `Mp4Downloader-v0.1.0-third-party-sources.zip` containing complete matching FFmpeg/external media-library sources, preserved upstream sources, exact commit/hash manifests, build scripts, lockfiles, compiler/configuration records and original notices. See `docs/third-party-builds.md`. There are no upstream source patches; build/configuration property overrides are documented. Source access does not depend solely on future upstream availability.

Exact executable/library SHA256, version and origin are in `THIRD_PARTY_MANIFEST.json` inside the Windows ZIP and `tools/binary-pins.json`. Original NuGet metadata/license/notices are preserved in `licenses/nuget/`. WebView2 Evergreen Runtime itself is not redistributed. First-party MIT rights do not replace any third-party terms.

Do not substitute the previous Gyan FFmpeg, yt-dlp.exe or Deno. Release gates reject nonfree/unreviewed FFmpeg libraries, mismatched fixed binary/source hashes, missing notices/build records and incomplete assets. Recipients retain the original rights to modify and redistribute GPL tools; separate Microsoft component terms do not restrict those independent tools or their sources.
