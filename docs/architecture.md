# MP4 Downloader v0.2

Status: implementation branch; no production release, no automatic merge. Company/product identifiers: TanakaNote / Mp4Downloader. Windows 11 x64, standard user only.

## Product and isolation

Obtain legitimately accessible, non-DRM VOD and save a validated MP4. No application backend, accounts, paid API, Cloudflare, telemetry, analytics or crash upload. OS protection and browser runtime may communicate according to Windows settings. No dependencies on T-ROOM or Downloader 1/2 code, data, profiles, CI or releases. Project references inside this new repository are allowed; cross-repository references are forbidden.

## Frozen dependencies

Checked official release/registry metadata on 2026-09-26:

| Component | Selected version | Source |
|---|---|---|
| .NET SDK | 10.0.401 | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Windows App SDK | 2.5.1 | https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1 |
| WebView2 SDK | 1.0.4191.47 | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47 |
| Windows SDK BuildTools | 10.0.28000.2705 | https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.28000.2705 |
| yt-dlp Windows binary | 2026.08.19 | https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19 |
| Deno | 2.9.7 | https://github.com/denoland/deno/releases/tag/v2.9.7 |
| FFmpeg / ffprobe | 9.0.2 Gyan essentials | https://www.gyan.dev/ffmpeg/builds/ |
| N_m3u8DL-RE | 0.6.0-beta | https://github.com/nilaoda/N_m3u8DL-RE/releases/tag/v0.6.0-beta |

N_m3u8DL-RE upstream currently offers a beta-tagged release, not a stable-tagged alternative. It is explicitly pinned as a beta exception and exercised using local HLS/DASH fixtures. Do not label it stable. NuGet transitive dependencies are locked in packages.lock.json. Downloaded archives have pinned SHA-256 values; extracted binaries are verified before execution. No runtime auto-update or remote EJS components. Official yt-dlp Windows binary includes EJS; Deno runs the built-in solver without granting broad permissions. WebView2 Evergreen is the explicit security-servicing exception to runtime version freeze.

## Packaging

Unpackaged folder, self-contained .NET and Windows App SDK, win-x64; no installer or elevation required. WebView2 Evergreen is detected when the browser resolver is invoked; missing runtime produces an error with Microsoft installation guidance in Settings. The app does not install it or change Defender settings. Publish is a local validation output, not a GitHub Release.

## Dependency direction and responsibilities

Core: platform-independent domain models, contracts, planner and coordinator; no UI/browser/tool process types. Infrastructure: HTTP, resolvers, acquisition adapters, media process implementation, Defender, storage. App: composition root, WinUI UI and WebView2 adapter. The coordinator is the only owner of final job success/failure. UI only supplies input and user decisions.

Resolver result is a transient PlaybackCandidate containing related video/audio tracks, protection, viability, evidence, known duration/resolution and an authentication-context reference. Never serialize the model into history. Acquisition planning is separate from MP4 planning. External muxing is intermediate; the Media Pipeline always probes and validates the final product.

## State and fallback

Resolving -> optional Selecting -> Downloading -> Probing -> Normalizing -> Validating -> Scanning -> Saving -> Completed. Any pre-commit cancellation/failure leads to cleanup. Only the successful filesystem commit makes a job successful; a history write failure is a warning after commit.

Resolver order: input classification/Direct, Manifest, yt-dlp, static HTML, WebView2. Direct manifest inputs skip the Direct media probe. A viable candidate needs main-playback evidence and an acquisition plan; incomplete candidates proceed to the next resolver. Ambiguous candidates require explicit selection. HTML parses video/source elements only; arbitrary JavaScript scraping is deliberately excluded.

Direct uses HttpClient. Trusted parallel ranges require known size, a strong ETag, identity encoding and a valid 206 probe. Connection upper bounds are 1/4/8/16 for <32MiB/32MiB/256MiB/1GiB. Every chunk checks byte range, length, total and ETag. All chunks are discarded before single-stream restart on range failure. A 429 reduces effective concurrency to one and respects bounded Retry-After; an excessive delay stops without an early retry. Connection/5xx retries are bounded. Single-stream retries restart from zero. Different candidates/engines never share partial files.

N_m3u8DL-RE is the primary public HLS/DASH acquisition engine. It receives locally snapshotted, validated clear manifests, disables logs/metadata, checks segment counts, excludes subtitles and uses bounded connections. No key/decryption tool options are constructed. Authenticated HLS uses the internal HTTP engine; authenticated DASH is unsupported until external redirect/credential safety is verified. Internal HLS supports clear VOD TS/fMP4 with a single map and no byte ranges/discontinuities; advanced clear playlists use N_m3u8DL-RE.

yt-dlp site extraction and acquisition are separate adapters. Site extraction does not read existing browser cookies, config, plugins or remote components. Extracted HTTP tracks are acquired by the direct engine; extracted manifests are protection-checked. The yt-dlp acquisition adapter is not selected by default: generic acquisition can reclassify a remote resource and implicitly handle encrypted HLS, so it remains disabled until that behavior can be safely constrained. Tool postprocessors do not choose the final MP4 policy.

Query-bearing/token-like manifests remain in memory: HLS uses the internal engine, and DASH is rejected if safe external delivery would require persisting those URLs. Do not write signed manifests to disk merely to expand compatibility. Re-resolution of an expired extractor URL is limited to one attempt, same video ID and plausible matching duration.

Protection: known DRM stops; HLS encryption is a different unsupported result. Live, LL-HLS, batch playlists, dynamic/multi-period DASH, unsupported schemes and audio-only input are rejected. Manifest relative URLs follow normal URI resolution; original query parameters are not copied indiscriminately.

## Browser and authentication

Dedicated profile: %LOCALAPPDATA%/TanakaNote/Mp4Downloader/WebView2. Password autosave/general autofill disabled. No elevated browser, host object, web-message native proxy, arbitrary download or permission grant. Current host is visible. User performs login/playback and asks the app to inspect candidates. HTTP currentSrc and manifest responses are used; blob/MSE fragment URLs are never treated as complete direct files. Network candidates alone do not prove a direct main video. No large response body capture.

Transient credentials reside in AuthVault. Cookie Domain/Path/Secure/expiry are enforced by CookieContainer. Authorization is origin-bound, reevaluated at each redirect, never sent to an external engine, and never sent on HTTPS->HTTP downgrade. Source cookies are not copied to unrelated CDNs. No cookie file is needed in the current implementation; a future verified external cookie-file route must use private ACLs, bounded lifetime and crash recovery.

## Windows11-Common MP4

MP4, H.264 Baseline/Main/High, 8bit yuv420p, level <=5.2, AAC-LC up to six channels when audio is present, faststart. Video without audio is allowed. Current conservative resolution ceiling: 4096x2304; no silent downscale. HDR/wide-gamut BT.2020 requiring processing is rejected; no tone mapping.

PASS_THROUGH: compatible complete MP4 and faststart, no track composition change. Additional video/audio/subtitle/attachment/data streams require explicit selection and remux rather than pass-through. REMUX: compatible streams but container, mux, faststart or normalization required. PARTIAL_TRANSCODE: only the incompatible selected stream is encoded. FULL_TRANSCODE: both selected video and audio need encoding. Encoding defaults: libx264 CRF 19, medium; AAC-LC 192kbps only when encoding audio. CRF19 balances quality and CPU cost without aggressive size targeting. Keep frame timing/VFR, resolution and aspect, pad odd dimensions only as needed, use explicit stream mappings, no synthetic silence/subtitles/attachments/data. FFmpeg input is local, file protocol only. Existing streams that meet the profile are copied.

Validation checks completion evidence, existence/size, video, expected audio, MP4/profile/faststart, plausible finite duration and bounded duration deviation (max 2s or 2%). Extra bounded decode check is used for very short/unknown-duration inputs. This is not a guarantee that every frame was decoded; routine full decoding is intentionally avoided.

## Defender and process execution

Scan the final file with MpCmdRun custom scan + DisableRemediation. Results: Clean, ThreatDetected, ScanUnavailable, ScanError. Only verified Clean may commit. Interpret command completion/output and hash/existence as well as exit code; unexpected/localized output fails closed. Never provide ignore-and-save, AV exclusions, settings changes or elevation. Final scanning does not sandbox earlier media parsers.

External tools run without a shell, with separate arguments, bounded stdout/stderr, deadlines, cancellation and a Windows kill-on-close Job Object. Raw commands/output/JSON are never written to normal logs. Processes/handles stop before deleting job files. These constraints are resource/lifecycle protections, not a claim of full OS sandboxing.

## Storage and privacy

Jobs: %LOCALAPPDATA%/TanakaNote/Mp4Downloader/Jobs/<GUID>. Downloads location uses the Windows Known Folder API; selected destination is persisted independently. Temp and destination capacity are checked, accounting for simultaneous files. Sanitized Windows filenames, no-overwrite commit and collision retry preserve existing files.

Same volume: rename only after validation and scan. Cross volume: copy to a uniquely named destination staging file, flush, compare size/SHA256, close handles, then rename within destination volume without overwriting. Volume identity uses Windows volume APIs, including mount points. UNC destinations are currently rejected. Filesystems may provide weaker guarantees; do not claim power-loss durability.

History is an allowlist of title, host, filename, size, resolution, duration, engine, processing mode, status, completion time and saved path. No original URL/query, token, cookie, Authorization, passwords or process JSON. Saved paths are local metadata. Keep at most 500 entries; user can clear history/browser data. No automatic job resumption with persisted signed URLs.

Job leases prevent recovery from deleting active jobs. Normal termination cleans up; crashed inactive jobs are recovered on next start. Reparse-point job roots are excluded. Cross-volume destination staging paths are recorded per job so crash recovery can remove only matching app-owned staging filenames. Cleanup errors are retried; OS locks can defer cleanup. Never delete unrelated files.

## Validation and release gates

Use local HTTP fixtures and short synthetic media for repeatable tests. Real-site support varies with website changes and needs a small representative manual check before a production release. CI restores locked packages, installs hash-pinned tools, tests and publishes the self-contained app. The GUI/WebView2 diagnostic runs separately on a non-elevated local desktop; it is not run unconditionally on hosted Windows runners with different privilege/desktop conditions. There is no merge, deployment or GitHub Release action. Cross-volume copy is tested with injected volume identities; a physical second-volume test is a separate manual check.

Before actual public binary distribution, complete corresponding-source/license packaging for all third-party binary dependencies, validate on a clean Windows 11 machine, verify real Defender behavior and representative browser/site flows. See THIRD_PARTY_NOTICES.md. No claims of these checks until actually executed.
