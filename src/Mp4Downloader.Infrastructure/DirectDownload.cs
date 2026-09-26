using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed record DirectProbe(Uri Url, long? Size, bool Range, string? ETag, string Mime, byte[] Prefix);
public sealed class DirectResolver(HttpTransport http) : IResolver
{
 public string Name => "Direct";
 public static async Task<DirectProbe> ProbeAsync(HttpTransport http, Uri url, string? auth, CancellationToken ct)
 {
  using var response = await http.SendAsync(url, auth, new RangeHeaderValue(0, 65535), null, ct);
  HttpTransport.RequireSuccess(response);
  var range = response.Content.Headers.ContentRange;
  var validRange = response.StatusCode == HttpStatusCode.PartialContent && range is { Unit: "bytes", From: 0, To: not null, Length: > 0 };
  var size = validRange ? range!.Length : response.StatusCode == HttpStatusCode.OK ? response.Content.Headers.ContentLength : null;
  await using var body = await response.Content.ReadAsStreamAsync(ct);
  using var prefix = new MemoryStream();
  var buffer = new byte[8192];
  while (prefix.Length < 65536)
  {
   var read = await HttpTransport.ReadWithTimeoutAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, 65536 - prefix.Length)), ct);
   if (read == 0) break;
   prefix.Write(buffer, 0, read);
  }
  return new(response.RequestMessage?.RequestUri ?? url, size, validRange && response.Content.Headers.ContentEncoding.Count == 0,
   response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null, response.Content.Headers.ContentType?.MediaType ?? "", prefix.ToArray());
 }
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct)
 {
  if (page.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || page.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return [];
  DirectProbe probe;
  try { probe = await ProbeAsync(http, page, null, ct); }
  catch (DownloadFailure ex) when (ex.Code == FailureCode.AccessRequired) { return []; }
  var text = Encoding.UTF8.GetString(probe.Prefix.AsSpan(0, Math.Min(256, probe.Prefix.Length))).TrimStart();
  if (text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return [];
  var magic = probe.Prefix.Length > 12 && (Encoding.ASCII.GetString(probe.Prefix, 4, 4) == "ftyp" || Encoding.ASCII.GetString(probe.Prefix, 0, 4) == "RIFF" || probe.Prefix.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }));
  if (!probe.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !magic) return [];
  return [new("direct:" + page.GetLeftPart(UriPartial.Path), SafeFileName.Base(Path.GetFileNameWithoutExtension(page.LocalPath)), page,
   MediaKind.Direct, [new("main", probe.Url, "video")], Protection.Clear, "Direct media MIME/signature", true)];
 }
}

public sealed class DirectDownloadEngine(HttpTransport http, long parallelThreshold = 32L * 1024 * 1024) : IDownloadEngine
{
 public string Name => "DirectHttp";
 public async Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct)
 {
  var files = new List<string>();
  foreach (var track in plan.Candidate.Tracks)
  {
   var path = Path.Combine(directory, $"{files.Count}-{track.Role}.media");
   var probe = await DirectResolver.ProbeAsync(http, track.Url, plan.Candidate.AuthContextId, ct);
   if (probe.Size is > 0)
   {
    var drive = new DriveInfo(Path.GetPathRoot(directory)!);
    if (drive.AvailableFreeSpace < probe.Size * 2 + 64L * 1024 * 1024) throw new DownloadFailure(FailureCode.DiskFull, "作業領域の空き容量が不足しています。");
   }
   var parallel = probe.Range && probe.ETag is not null && probe.Size >= parallelThreshold;
   if (parallel)
   {
    try { await ParallelAsync(probe, plan.Candidate.AuthContextId, path, progress, ct); }
    catch (DownloadFailure ex) when (ex.Code is FailureCode.Corrupt or FailureCode.Network or FailureCode.RateLimited)
    { await SingleAsync(probe.Url, plan.Candidate.AuthContextId, path, progress, ct); }
   }
   else await SingleAsync(probe.Url, plan.Candidate.AuthContextId, path, progress, ct);
   files.Add(path);
  }
  return new(files, Name, true, plan.Candidate.Duration);
 }
 private async Task ParallelAsync(DirectProbe probe, string? auth, string path, IProgress<JobProgress> progress, CancellationToken ct)
 {
  var size = probe.Size!.Value;
  var connections = size >= 1024L * 1024 * 1024 ? 16 : size >= 256L * 1024 * 1024 ? 8 : 4;
  var chunks = Enumerable.Range(0, connections).Select(i => path + $".{i}.part").ToArray();
  using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
  long total = 0;
  try
  {
   var tasks = Enumerable.Range(0, connections).Select(async i =>
   {
    try
    {
     var from = size * i / connections;
     var to = size * (i + 1) / connections - 1;
     using var response = await http.SendAsync(probe.Url, auth, new RangeHeaderValue(from, to), probe.ETag, stop.Token);
     if (response.StatusCode == HttpStatusCode.TooManyRequests)
     { await DelayRetryAsync(response, stop.Token); throw new DownloadFailure(FailureCode.RateLimited, "接続数を減らして再試行します。"); }
     var range = response.Content.Headers.ContentRange;
     if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != from || range.To != to || range.Length != size ||
       response.Headers.ETag?.ToString() != probe.ETag || response.Content.Headers.ContentEncoding.Count != 0)
      throw new DownloadFailure(FailureCode.Corrupt, "Range応答が一致しません。");
     await using var input = await response.Content.ReadAsStreamAsync(stop.Token);
     await using var output = new FileStream(chunks[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
     var buffer = new byte[65536];
     long received = 0;
     while (true)
     {
      var read = await HttpTransport.ReadWithTimeoutAsync(input, buffer, stop.Token);
      if (read == 0) break;
      received += read;
      if (received > to - from + 1) throw new DownloadFailure(FailureCode.Corrupt, "Rangeサイズが一致しません。");
      await output.WriteAsync(buffer.AsMemory(0, read), stop.Token);
      var downloaded = Interlocked.Add(ref total, read);
      progress.Report(new(JobStage.Downloading, "動画を取得しています", (double)downloaded / size));
     }
     if (received != to - from + 1) throw new DownloadFailure(FailureCode.Corrupt, "Rangeデータが不足しています。");
    }
    catch { stop.Cancel(); throw; }
   }).ToArray();
   try { await Task.WhenAll(tasks); }
   catch when (!ct.IsCancellationRequested)
   {
    var failure = tasks.SelectMany(t => t.Exception?.InnerExceptions ?? []).OfType<DownloadFailure>().FirstOrDefault();
    throw failure ?? new DownloadFailure(FailureCode.Network, "並列取得を完了できませんでした。");
   }
   await using var result = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
   foreach (var chunk in chunks) { await using var input = File.OpenRead(chunk); await input.CopyToAsync(result, ct); }
   if (result.Length != size) throw new DownloadFailure(FailureCode.Corrupt, "動画サイズが一致しません。");
  }
  finally { foreach (var chunk in chunks) if (File.Exists(chunk)) File.Delete(chunk); }
 }
 private async Task SingleAsync(Uri uri, string? auth, string path, IProgress<JobProgress> progress, CancellationToken ct)
 {
  for (var attempt = 0; attempt < 3; attempt++)
  {
   try
   {
    using var response = await http.SendAsync(uri, auth, null, null, ct);
    if (response.StatusCode == HttpStatusCode.TooManyRequests) { await DelayRetryAsync(response, ct); throw new DownloadFailure(FailureCode.RateLimited, "動画サーバーが混雑しています。"); }
    HttpTransport.RequireSuccess(response);
    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count > 0) throw new DownloadFailure(FailureCode.Corrupt, "動画の取得条件が一致しません。");
    await using var input = await response.Content.ReadAsStreamAsync(ct);
    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
    var buffer = new byte[65536];
    while (true)
    {
     var read = await HttpTransport.ReadWithTimeoutAsync(input, buffer, ct);
     if (read == 0) break;
     await output.WriteAsync(buffer.AsMemory(0, read), ct);
     if (response.Content.Headers.ContentLength is > 0 and var length)
      progress.Report(new(JobStage.Downloading, "動画を取得しています", Math.Min(1, (double)output.Length / length)));
    }
    if (output.Length == 0 || response.Content.Headers.ContentLength is { } expected && output.Length != expected) throw new DownloadFailure(FailureCode.Corrupt, "動画データが不足しています。");
    return;
   }
   catch (DownloadFailure ex) when (attempt < 2 && ex.Code is FailureCode.Network or FailureCode.RateLimited or FailureCode.Corrupt)
   { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
  }
 }
 private static async Task DelayRetryAsync(HttpResponseMessage response, CancellationToken ct)
 {
  var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(2);
  if (delay > TimeSpan.FromSeconds(60)) throw new DownloadFailure(FailureCode.RateLimited, "動画サーバーが待機を要求しています。時間を置いて再試行してください。");
  if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
 }
}
