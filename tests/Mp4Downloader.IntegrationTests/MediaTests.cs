using System.Net;
using System.Net.Sockets;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using Xunit;

namespace Mp4Downloader.IntegrationTests;

public class MediaTests
{
 private static readonly Lazy<Task<Fixtures>> fixtures = new(CreateFixturesAsync);
 [Theory]
 [InlineData("good.mp4", Mp4Mode.PassThrough)]
 [InlineData("late.mp4", Mp4Mode.Remux)]
 [InlineData("good.mkv", Mp4Mode.Remux)]
 [InlineData("good.ts", Mp4Mode.Remux)]
 [InlineData("good.mov", Mp4Mode.Remux)]
 [InlineData("good.webm", Mp4Mode.FullTranscode)]
 [InlineData("good.wmv", Mp4Mode.FullTranscode)]
 [InlineData("opus.mkv", Mp4Mode.PartialTranscode)]
 [InlineData("silent.mp4", Mp4Mode.PassThrough)]
 public async Task RealMediaUsesMinimalProcessingAndValidates(string name, Mp4Mode expected)
 {
  var f = await fixtures.Value;
  var job = Path.Combine(f.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
  try
  {
   var result = await f.Pipeline.NormalizeAsync(new([Path.Combine(f.Root, name)], "fixture", true, 3), job, new Progress<JobProgress>(), default);
   Assert.Equal(expected, result.Plan.Mode); Assert.True(Mp4Planner.Compatible(result.Media));
   Assert.Equal(name == "silent.mp4", result.Media.Audio is null);
  }
  finally { Directory.Delete(job, true); }
 }
 [Theory]
 [InlineData("hdr.mp4", FailureCode.Hdr)]
 [InlineData("audio.m4a", FailureCode.AudioOnly)]
 [InlineData("broken.mp4", FailureCode.Corrupt)]
 public async Task UnsafeOrUnsupportedMediaCannotNormalize(string name, FailureCode code)
 {
  var f = await fixtures.Value;
  var failure = await Assert.ThrowsAsync<DownloadFailure>(() => f.Pipeline.NormalizeAsync(new([Path.Combine(f.Root, name)], "fixture", true), f.Root, new Progress<JobProgress>(), default));
  Assert.Equal(code, failure.Code);
 }
 [Theory]
 [InlineData("stream.m3u8", "N_m3u8DL-RE")]
 [InlineData("stream.m3u8", "HlsHttp")]
 [InlineData("stream.mpd", "N_m3u8DL-RE")]
 public async Task RealStreamingFixtureDownloadsPairedTracksAndNormalizes(string name, string engine)
 {
  var f = await fixtures.Value;
  using var server = new FixtureServer(f.Root);
  using var http = new HttpTransport();
  var resolver = new ManifestResolver(http);
  var candidates = await resolver.ResolveAsync(new(server.Base, name), default);
  var candidate = Assert.Single(candidates);
  Assert.Equal(Protection.Clear, candidate.Protection);
  var job = Path.Combine(f.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
  try
  {
   IDownloadEngine downloader = engine == "HlsHttp" ? new HlsDownloadEngine(http) : new ManifestDownloadEngine(f.Tools, f.Processes);
   var acquired = await downloader.DownloadAsync(new(candidate, engine), job, new Progress<JobProgress>(), default);
   var result = await f.Pipeline.NormalizeAsync(acquired, job, new Progress<JobProgress>(), default);
   Assert.True(Mp4Planner.Compatible(result.Media)); Assert.NotNull(result.Media.Audio); Assert.InRange(result.Media.Video!.Duration, 2.9, 3.2);
  }
  finally { Directory.Delete(job, true); }
 }
 [Fact] public async Task HlsMasterPairsExternalAudioAndRejectsMissingSegments()
 {
  var f = await fixtures.Value;
  using var server = new FixtureServer(f.Root); using var http = new HttpTransport();
  var candidates = await new ManifestResolver(http).ResolveAsync(new(server.Base, "master.m3u8"), default);
  Assert.Equal(2, Assert.Single(candidates).Tracks.Count);
  var missing = await new ManifestResolver(http).ResolveAsync(new(server.Base, "missing.m3u8"), default);
  var job = Path.Combine(f.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
  try { await Assert.ThrowsAsync<DownloadFailure>(() => new HlsDownloadEngine(http).DownloadAsync(new(missing[0], "HlsHttp"), job, new Progress<JobProgress>(), default)); }
  finally { Directory.Delete(job, true); }
 }
 [Fact] public async Task ProcessTimeoutStopsTool()
 {
  var f = await fixtures.Value;
  var failure = await Assert.ThrowsAsync<DownloadFailure>(() => f.Processes.RunAsync(f.Tools.Find("deno"), ["eval", "await new Promise(r=>setTimeout(r,10000))"], f.Root, TimeSpan.FromMilliseconds(200), default));
  Assert.Equal(FailureCode.Tool, failure.Code);
 }
 private sealed record Fixtures(string Root, ToolCatalog Tools, ProcessRunner Processes, MediaPipeline Pipeline);
 private static async Task<Fixtures> CreateFixturesAsync()
 {
  var repo = new DirectoryInfo(AppContext.BaseDirectory);
  while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "global.json"))) repo = repo.Parent;
  if (repo is null) throw new InvalidOperationException("Repository fixture root not found.");
  var root = Path.Combine(repo.FullName, "artifacts", "fixtures"); Directory.CreateDirectory(root);
  var tools = new ToolCatalog(Path.Combine(repo.FullName, "tools", "bin")); var processes = new ProcessRunner();
  async Task Make(string output, params string[] args)
  {
   var result = await processes.RunAsync(tools.Find("ffmpeg"), new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args).Append(Path.Combine(root, output)), root, TimeSpan.FromMinutes(1), default);
   Assert.True(result.ExitCode == 0, "Fixture generation failed: " + output);
  }
  await Make("good.mp4", "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "12", "-c:a", "aac", "-profile:a", "aac_low", "-movflags", "+faststart");
  var input = Path.Combine(root, "good.mp4");
  foreach (var output in new[] { "good.mkv", "good.ts", "good.mov", "late.mp4" }) await Make(output, "-i", input, "-c", "copy");
  await Make("good.webm", "-i", input, "-c:v", "libvpx-vp9", "-c:a", "libopus");
  await Make("good.wmv", "-i", input, "-c:v", "wmv2", "-c:a", "wmav2");
  await Make("opus.mkv", "-i", input, "-c:v", "copy", "-c:a", "libopus");
  await Make("silent.mp4", "-i", input, "-an", "-c:v", "copy", "-movflags", "+faststart");
  await Make("hdr.mp4", "-i", input, "-c", "copy", "-bsf:v", "h264_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9", "-movflags", "+faststart");
  await Make("audio.m4a", "-i", input, "-vn", "-c:a", "copy");
  await Make("stream.m3u8", "-i", input, "-c", "copy", "-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(root, "segment%03d.ts"));
  await Make("stream.mpd", "-i", input, "-c", "copy", "-f", "dash", "-seg_duration", "1");
  await File.WriteAllTextAsync(Path.Combine(root, "broken.mp4"), "This is not a media file.");
  await File.WriteAllTextAsync(Path.Combine(root, "master.m3u8"), "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"main\",DEFAULT=YES,URI=\"stream.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=500000,AUDIO=\"audio\"\nstream.m3u8\n");
  await File.WriteAllTextAsync(Path.Combine(root, "missing.m3u8"), "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nnot-present.ts\n#EXT-X-ENDLIST\n");
  return new(root, tools, processes, new(tools, processes, new(tools, processes)));
 }
 private sealed class FixtureServer : IDisposable
 {
  private readonly HttpListener listener = new(); private readonly string root;
  public Uri Base { get; }
  public FixtureServer(string root)
  {
   this.root = root;
   var portPicker = new TcpListener(IPAddress.Loopback, 0); portPicker.Start(); var port = ((IPEndPoint)portPicker.LocalEndpoint).Port; portPicker.Stop();
   Base = new($"http://127.0.0.1:{port}/"); listener.Prefixes.Add(Base.AbsoluteUri); listener.Start(); _ = ServeAsync();
  }
  private async Task ServeAsync()
  {
   try
   {
    while (listener.IsListening)
    {
     var context = await listener.GetContextAsync();
     var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'))));
     if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { context.Response.StatusCode = 404; context.Response.Close(); continue; }
     var bytes = await File.ReadAllBytesAsync(path);
     context.Response.ContentLength64 = bytes.Length;
     context.Response.ContentType = Path.GetExtension(path) == ".m3u8" ? "application/vnd.apple.mpegurl" : "application/octet-stream";
     await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
   }
   catch (HttpListenerException) { }
   catch (ObjectDisposedException) { }
  }
  public void Dispose() => listener.Close();
 }
}
