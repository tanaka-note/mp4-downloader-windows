using System.Text.Json;
using System.Text.RegularExpressions;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class AcquisitionPlanner : IAcquisitionPlanner
{
 public IReadOnlyList<AcquisitionPlan> Plan(PlaybackCandidate candidate)
 {
  DownloadCoordinator.CheckProtection(candidate);
  return candidate.Kind switch
  {
   MediaKind.Direct => [new(candidate, "DirectHttp")],
   MediaKind.Hls => candidate.AuthContextId is null && !ManifestPrivacy.RequiresMemory(candidate) ? [new(candidate, "N_m3u8DL-RE"), new(candidate, "HlsHttp")] : [new(candidate, "HlsHttp")],
   MediaKind.Dash when candidate.AuthContextId is null && !ManifestPrivacy.RequiresMemory(candidate) => [new(candidate, "N_m3u8DL-RE")],
   _ => throw new DownloadFailure(FailureCode.Unsupported, "この認証または配信方式を安全に取得する経路がありません。")
  };
 }
}
public sealed class YtDlpResolver(ToolCatalog tools, ProcessRunner processes, ManifestResolver manifests, AuthVault vault, Func<string> workingDirectory) : IResolver
{
 public string Name => "yt-dlp";
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct)
 {
  var args = YtArguments.Base(tools).Concat(["--dump-single-json", "--skip-download", "--format", "bestvideo+bestaudio/best", "--", page.AbsoluteUri]);
  ProcessResult result;
  try { result = await processes.RunAsync(tools.Find("yt-dlp"), args, workingDirectory(), TimeSpan.FromMinutes(2), ct); }
  catch (DownloadFailure ex) when (ex.Code == FailureCode.Tool) { throw new DownloadFailure(FailureCode.Network, "サイト解析を完了できませんでした。"); }
  if (result.ExitCode != 0) return [];
  JsonDocument json;
  try { json = JsonDocument.Parse(result.Output); }
  catch (JsonException) { throw new DownloadFailure(FailureCode.NoMatch, "サイト解析結果を読み取れませんでした。"); }
  using (json)
  {
   var root = json.RootElement;
   if (root.TryGetProperty("_type", out var type) && type.GetString() is "playlist" or "multi_video") throw new DownloadFailure(FailureCode.Unsupported, "動画一覧や複数動画の一括取得には対応していません。");
   if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True) throw new DownloadFailure(FailureCode.Unsupported, "Live動画には対応していません。");
   var drm = root.TryGetProperty("has_drm", out var protectedValue) && protectedValue.ValueKind == JsonValueKind.True;
   if (drm) return [new("protected", "動画", page, MediaKind.Site, [], Protection.Drm, "Extractor DRM declaration", true)];
   var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "video" : "video";
   var title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? "動画" : "動画";
   var formatList = root.TryGetProperty("requested_formats", out var formats) ? formats.EnumerateArray().ToArray() : [root];
   var tracks = new List<Track>();
   var auth = new AuthContext();
   var hasAuth = false;
   foreach (var format in formatList)
   {
    drm |= format.TryGetProperty("has_drm", out var hasDrm) && hasDrm.ValueKind == JsonValueKind.True;
    if (!format.TryGetProperty("url", out var rawUrl) || !Uri.TryCreate(rawUrl.GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) continue;
    var protocol = format.TryGetProperty("protocol", out var proto) ? proto.GetString() ?? "" : "";
    if (protocol.Contains("m3u8", StringComparison.Ordinal) || protocol.Contains("dash", StringComparison.Ordinal))
    {
     var manifestUrl = format.TryGetProperty("manifest_url", out var manifest) ? manifest.GetString() : url.AbsoluteUri;
     if (manifestUrl is not null) return (await manifests.ResolveAsync(UrlPolicy.Parse(manifestUrl), page, null, ct)).Select(c => c with { Title = title, Id = id, Refreshable = true }).ToArray();
     continue;
    }
    if (protocol is not ("http" or "https" or "")) continue;
    if (format.TryGetProperty("http_headers", out var headers))
     foreach (var header in headers.EnumerateObject())
      if (header.Name is "User-Agent" or "Referer") { auth.Headers[header.Name + "|" + url.GetLeftPart(UriPartial.Authority)] = (url, header.Value.GetString() ?? ""); hasAuth = true; }
      else if (header.Name is "Authorization" or "Cookie") throw new DownloadFailure(FailureCode.AccessRequired, "このサイトの認証条件は現在安全に受け渡しできません。");
    var video = !format.TryGetProperty("vcodec", out var vcodec) || vcodec.GetString() != "none";
    tracks.Add(new(format.TryGetProperty("format_id", out var formatId) ? formatId.GetString() ?? "main" : "main", url, video ? "video" : "audio"));
   }
   if (drm) return [new(id, title, page, MediaKind.Site, tracks, Protection.Drm, "Extractor DRM declaration", true)];
   if (!tracks.Any(t => t.Role == "video")) return [];
   var duration = root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : (double?)null;
   return [new(id, title, page, MediaKind.Direct, tracks, Protection.Clear, "Site extractor selected complete video/audio", true, duration,
    AuthContextId: hasAuth ? vault.Add(auth) : null, Refreshable: true)];
  }
 }
}
internal static class YtArguments
{
 public static string[] Base(ToolCatalog tools) => ["--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-update", "--no-playlist", "--no-progress", "--no-warnings",
  "--no-cache-dir", "--no-js-runtimes", "--js-runtimes", "deno:" + tools.Find("deno"), "--socket-timeout", "20", "--retries", "2", "--ffmpeg-location", tools.Directory];
}
// Only already-classified public direct resources are eligible. This adapter never reselects a site video.
public sealed class YtDlpDownloadEngine(ToolCatalog tools, ProcessRunner processes) : IDownloadEngine
{
 public string Name => "YtDlp";
 public async Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct)
 {
  if (plan.Candidate.Kind != MediaKind.Direct || plan.Candidate.AuthContextId is not null) throw new DownloadFailure(FailureCode.Unsupported, "この経路では認証付き取得を行えません。");
  var files = new List<string>();
  foreach (var track in plan.Candidate.Tracks)
  {
   var path = Path.Combine(directory, $"track-{files.Count}.media");
   var args = YtArguments.Base(tools).Concat(["--use-extractors", "generic", "--downloader", "native", "--no-part", "--no-write-info-json", "--no-write-playlist-metafiles", "--no-write-thumbnail", "--no-write-subs", "--output", path, "--", track.Url.AbsoluteUri]);
   var result = await processes.RunAsync(tools.Find("yt-dlp"), args, directory, TimeSpan.FromHours(6), ct);
   if (result.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0) throw new DownloadFailure(FailureCode.Network, "補助Engineで動画を取得できませんでした。");
   files.Add(path);
  }
  return new(files, Name, true, plan.Candidate.Duration);
 }
}
public sealed class ManifestDownloadEngine(ToolCatalog tools, ProcessRunner processes) : IDownloadEngine
{
 public string Name => "N_m3u8DL-RE";
 public async Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct)
 {
  DownloadCoordinator.CheckProtection(plan.Candidate);
  if (plan.Candidate.AuthContextId is not null) throw new DownloadFailure(FailureCode.Unsupported, "外部Engineへの認証情報転送は行いません。");
  if (ManifestPrivacy.RequiresMemory(plan.Candidate)) throw new DownloadFailure(FailureCode.Unsupported, "機密URLを含むManifestを外部Engineへ渡せません。");
  var files = new List<string>();
  var sources = plan.Candidate.Kind == MediaKind.Dash ? new[] { plan.Candidate.Snapshot! } : plan.Candidate.Tracks.Select(t => t.Manifest!).ToArray();
  for (var i = 0; i < sources.Length; i++)
  {
   var work = Path.Combine(directory, $"stream-{i}");
   Directory.CreateDirectory(work);
   var manifest = Path.Combine(work, plan.Candidate.Kind == MediaKind.Dash ? "input.mpd" : "input.m3u8");
   if (string.IsNullOrEmpty(sources[i])) throw new DownloadFailure(FailureCode.Unsupported, "検証済みManifestがありません。");
   await File.WriteAllTextAsync(manifest, sources[i], ct);
   var args = new List<string> { manifest, "--save-dir", work, "--tmp-dir", Path.Combine(work, "segments"), "--save-name", "acquired", "--no-log", "--write-meta-json", "false",
    "--check-segments-count", "true", "--thread-count", "4", "--download-retry-count", "2", "--http-request-timeout", "20", "--disable-update-check", "--auto-select", "--drop-subtitle", "all", "--ffmpeg-binary-path", tools.Find("ffmpeg") };
   var result = await processes.RunAsync(tools.Find("N_m3u8DL-RE"), args, work, TimeSpan.FromHours(6), ct);
   if (result.ExitCode != 0) throw new DownloadFailure(FailureCode.Network, "Streaming動画を取得できませんでした。");
   var output = Directory.EnumerateFiles(work).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".mp4" or ".m4a" or ".ts" or ".mkv" or ".aac").ToArray();
   if (output.Length == 0) throw new DownloadFailure(FailureCode.Corrupt, "Streaming動画の完成ファイルがありません。");
   files.AddRange(output);
  }
  return new(files, Name, true, plan.Candidate.Duration);
 }
}

public static class ManifestPrivacy
{
 public static bool RequiresMemory(PlaybackCandidate candidate)
 {
  if (candidate.Tracks.Any(t => t.Url.Query.Length > 0)) return true;
  var text = candidate.Snapshot ?? string.Join('\n', candidate.Tracks.Select(t => t.Manifest ?? ""));
  // Conservative: any query-bearing resource or token-like template is kept out of disk snapshots.
  return Regex.IsMatch(text, @"https?://[^\s\""<>]*\?|(?i)(token|signature|authorization|cookie|password)\s*[:=]|(?:media|initialization|sourceURL|URI)\s*=\s*[\""'][^\""']*\?");
 }
}

public sealed class HlsDownloadEngine(HttpTransport http) : IDownloadEngine
{
 public string Name => "HlsHttp";
 public async Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct)
 {
  var files = new List<string>();
  foreach (var track in plan.Candidate.Tracks)
  {
   var text = track.Manifest ?? throw new DownloadFailure(FailureCode.Unsupported, "検証済みManifestがありません。");
   if (ManifestResolver.HlsProtection(text) != Protection.Clear) throw new DownloadFailure(FailureCode.Encrypted, "この暗号化方式には現在対応していません。");
   var lines = ManifestResolver.Lines(text);
   if (lines.Any(l => l.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) || l.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)))
    throw new DownloadFailure(FailureCode.Unsupported, "このHLS構成は内蔵Engineの対応範囲外です。");
   var path = Path.Combine(directory, $"hls-{files.Count}.media");
   await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
   var urls = lines.Where(l => !l.StartsWith('#')).ToList();
   var maps = lines.Where(l => l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)).Select(ManifestResolver.Attributes).ToList();
   if (maps.Count > 1 || maps.Any(m => m.ContainsKey("BYTERANGE"))) throw new DownloadFailure(FailureCode.Unsupported, "この初期化segment構成には対応していません。");
   if (maps.Count == 1) urls.Insert(0, maps[0]["URI"]);
   for (var i = 0; i < urls.Count; i++)
   {
    using var response = await http.SendAsync(UrlPolicy.Parse(urls[i]), plan.Candidate.AuthContextId, null, null, ct);
    HttpTransport.RequireSuccess(response);
    await using var input = await response.Content.ReadAsStreamAsync(ct);
    var before = output.Length;
    var buffer = new byte[65536];
    while (true) { var read = await HttpTransport.ReadWithTimeoutAsync(input, buffer, ct); if (read == 0) break; await output.WriteAsync(buffer.AsMemory(0, read), ct); }
    var received = output.Length - before;
    if (received == 0 || response.Content.Headers.ContentLength is { } expected && expected != received) throw new DownloadFailure(FailureCode.Corrupt, "動画segmentが欠損しています。");
    progress.Report(new(JobStage.Downloading, "動画segmentを取得しています", (double)(i + 1) / urls.Count));
   }
   files.Add(path);
  }
  return new(files, Name, true, plan.Candidate.Duration);
 }
}
