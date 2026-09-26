# Third-party build and redistribution record

The companion `Mp4Downloader-v0.1.0-third-party-sources.zip` preserves the exact downloaded source archives, SHA256/version/commit manifests, licenses, compiler/configuration records, lockfiles, and build scripts. It is served beside the Windows ZIP on the same GitHub Release. There are no application-source patches to the upstream media/streaming tools. Build property overrides are explicit in the scripts. Binary checksums are in `tools/binary-pins.json`; per-file publish provenance is in `THIRD_PARTY_MANIFEST.json` inside the Windows ZIP.

## FFmpeg and ffprobe

FFmpeg 9.0.2 plus x264 commit b35605ace3ddf7c1a5d67a2eb553f034aef41d55, libvpx 1.15.2 and Opus 1.5.2. Combined media executables are GPL-3.0-or-later. No nonfree component, Gyan binary, hardware SDK, TLS library, network client library, or other optional media library is included. Internal decoders/demuxers remain available; x264, VP9 and Opus encoding retain the regression fixtures. Network access is disabled because application engines acquire inputs first.

On Ubuntu 24.04 install the cross-compiler/build packages listed in `tools/media-provenance/build-packages.txt`, then run `bash tools/Build-Media.sh`. Use the exact package versions recorded there for the original compiler environment. The script downloads and verifies fixed sources before configuring and building. Archives can instead be supplied from the companion package by replacing only the download step with its checksum-verified local copies. Configuration and generated `config.h`/`config.mak` are preserved. GCC runtime code uses the GCC Runtime Library Exception; MinGW runtime notices are retained. System Windows DLLs are not bundled.

This provides corresponding sources and a repeatable build procedure, not a promise of bit-for-bit reproducibility across different compiler/OS paths. The distributed binary hashes remain fixed and are verified independently. The workflow `Build licensed media tools` records the original compilation.

## yt-dlp

The official 2026.08.19 zipimport release is used byte-for-byte, verified against its upstream SHA2-256SUMS. It is not the PyInstaller Windows executable. Core is Unlicense; its bundled EJS 0.8.0 includes Meriyah (ISC) and Astring (MIT). The official matching source tarball and EJS source are preserved. Invoke `tools/python/python.exe -I -B tools/yt-dlp ...`; the isolated official CPython 3.14.7 embeddable distribution is included with its full licenses. No pip/user packages, PyInstaller, curl_cffi, mutagen, or browser cookie store are used. Optional browser impersonation is not available; HTML and WebView2 fallbacks remain. Built-in EJS with fixed Node.js supports the JS path without remote component downloads.

## Python and Node.js

CPython 3.14.7 and Node.js v24.21.0 are unchanged official Windows distributions (Node's executable and complete LICENSE; npm is not shipped). Exact release source archives include their upstream build scripts and dependency pins; Python's Windows dependency versions are in `PCbuild/python.props`, Node's vendored sources are under `deps/`. Full distribution license texts, including Python's Microsoft VC runtime terms, are bundled. There is no local rebuild or claimed custom signing of those binaries. Run the upstream Windows build instructions in those archives to rebuild; upstream Microsoft-signed binaries cannot be bit-identically re-signed by this project.

## N_m3u8DL-RE

Tag v0.6.0-beta, commit df70f0b3da0c630bd413bf617e758051f6b64757, MIT. Extract its preserved source archive into `tools/cache/n-source`, then run `pwsh tools/Build-Streaming.ps1` with .NET SDK 10.0.401. The script supplies preserved dependency lockfiles and publishes an independent .NET 10.0.12 self-contained executable. It overrides upstream AOT/trimming properties; no upstream source edits or DRM feature additions were made. Spectre.Console/Ansi and System.CommandLine licenses and exact package metadata are in `licenses/nuget/`. The application passes only already-classified clear VOD manifests and never supplies decryption keys or license-server operations.

## Application runtimes

.NET 10.0.12 is MIT with its complete third-party notices. Windows App SDK and WebView2 SDK redistributable binaries retain their own Microsoft terms (not MIT/GPL relicensed). Official NuGet package versions/content hashes are locked in `packages.lock.json`; their nuspec metadata, license and notices are retained under `licenses/nuget`. Only binplaced runtime files are published; SDK build tools are not runtime requirements. WebView2 Evergreen Runtime itself is not redistributed. The first-party application source is MIT; independently launched GPL tools retain their GPL terms. No proprietary Microsoft source is claimed to be available.

## Release gates

`Test-LicenseGate.ps1` rejects missing/mismatched fixed binaries, missing notices/manifests/sources/build records, extra optional FFmpeg libraries, nonfree builds, stale yt-dlp.exe/Deno, and incomplete release assets. `Test-LicenseGate.Tests.ps1` checks negative cases. `Package.ps1 -ForRelease` must pass the gate with the companion source package. CI performs these gates before merge; final uploaded assets are downloaded and rechecked before publication.
