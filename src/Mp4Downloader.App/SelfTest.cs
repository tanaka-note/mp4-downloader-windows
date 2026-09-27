using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace Mp4Downloader.App;

// Explicit diagnostic mode, isolated from normal AppData, loopback only; never reads user browser data.
internal static class SelfTest
{
 public static async Task RunAsync(Window main, string root, bool releaseSmoke = false)
 {
  Directory.CreateDirectory(root);
  var report = Path.Combine(root, "self-test.json");
  BrowserWindow? browser = null;
  var checkpoint = "Startup";
  try
  {
   using var identity = WindowsIdentity.GetCurrent();
   var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
   if (elevated && !releaseSmoke) throw new InvalidOperationException("Browser diagnostics require a non-elevated user.");
   using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
   var tools = new ToolCatalog(Path.Combine(AppContext.BaseDirectory, "tools")); var runner = new ProcessRunner();
   var ytVersion = await runner.RunAsync(tools.Find("yt-dlp"), ["-I", "-B", tools.VerifyFile("yt-dlp"), "--ignore-config", "--no-update", "--version"], root, TimeSpan.FromSeconds(15), timeout.Token);
   if (ytVersion.ExitCode != 0 || ytVersion.Output.Trim() != "2026.08.19") throw new InvalidOperationException("Bundled yt-dlp startup/version failed.");
   var video = Path.Combine(root, "fixture.mp4");
   var created = await runner.RunAsync(tools.Find("ffmpeg"), ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=12", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", video], root, TimeSpan.FromSeconds(20), timeout.Token);
   if (created.ExitCode != 0) throw new InvalidOperationException("Fixture creation failed.");
   var ts = Path.ChangeExtension(video, ".ts");
   var segmented = await runner.RunAsync(tools.Find("ffmpeg"), ["-v", "error", "-nostdin", "-y", "-i", video, "-c", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "mpegts", ts], root, TimeSpan.FromSeconds(10), timeout.Token);
   if (segmented.ExitCode != 0) throw new InvalidOperationException("HLS fixture creation failed.");
   var probe = await new MediaProbe(tools, runner).ProbeAsync(video, timeout.Token);
   if (!Mp4Planner.Compatible(probe)) throw new InvalidOperationException("Media profile failed.");
   using var server = new HttpListener();
   var picker = new TcpListener(IPAddress.Loopback, 0); picker.Start(); var port = ((IPEndPoint)picker.LocalEndpoint).Port; picker.Stop();
   var basis = new Uri($"http://127.0.0.1:{port}/"); server.Prefixes.Add(basis.AbsoluteUri); server.Start();
   var serving = ServeAsync(server, video, timeout.Token);
   var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(root, "Browser"), null);
   var missingHandled = false;
   var missingFolder = Path.Combine(root, "MissingRuntime"); Directory.CreateDirectory(missingFolder);
   try { _ = await CoreWebView2Environment.CreateWithOptionsAsync(missingFolder, Path.Combine(root, "MissingProfile"), null); }
   catch (Exception) { missingHandled = true; }
   if (!missingHandled) throw new InvalidOperationException("Missing Runtime diagnostic unexpectedly succeeded.");
   var vault = new AuthVault(); using var http = new HttpTransport(vault, allowPrivateNetwork: true); var manifests = new ManifestResolver(http);
   var discovered = false;
   bool? privateBrowserBlocked = null;
   bool? publicBrowserNavigation = null;
   bool? invalidProxyReproduced = null;
   bool? hlsBrowserDiscovery = null;
   bool? changingBrowserDiscovery = null;
   bool? browserReplayHeaders = null;
   bool? browserHlsSave = null;
   if (!elevated)
   {
    checkpoint = "DirectBrowser";
    browser = new BrowserWindow(); browser.Activate();
    var candidates = await browser.ResolveAsync(environment, basis, http, manifests, vault, timeout.Token, fixtureMode: true);
    if (candidates.Count != 1 || candidates[0].Kind != MediaKind.Direct) throw new InvalidOperationException("Dynamic browser discovery failed.");
    discovered = true;
    browser.Close();
    browser = new BrowserWindow(); browser.Activate();
    checkpoint = "HlsBrowser";
    var hls = await browser.ResolveAsync(environment, new Uri(basis, "hls"), http, manifests, vault, timeout.Token, fixtureMode: true);
    if (hls.Count != 1 || hls[0].Kind != MediaKind.Hls) throw new InvalidOperationException("Dynamic HLS manifest discovery failed.");
    hlsBrowserDiscovery = true;
    browser.Close();
    var replayHandler = new DelayedManifestHandler();
    checkpoint = "ChangingBrowser";
    using var delayedHttp = new HttpTransport(vault, replayHandler, allowPrivateNetwork: true);
    browser = new BrowserWindow(); browser.Activate();
    var changing = await browser.ResolveAsync(environment, new Uri(basis, "race"), delayedHttp, new ManifestResolver(delayedHttp), vault, timeout.Token, fixtureMode: true);
    checkpoint = "ChangingBrowserValidation-" + replayHandler.FailureReason + "-Captured" + browser.CapturedRefererCount;
    if (changing.Count == 0 || changing.Any(c => c.Kind != MediaKind.Hls)) throw new InvalidOperationException("Changing browser candidate discovery failed.");
    changingBrowserDiscovery = true;
    checkpoint = "ReplayHeaders";
    if (!replayHandler.SawReplayHeaders) throw new InvalidOperationException("Browser request headers were not replayed.");
    browserReplayHeaders = true;
    browser.Close();
    checkpoint = "BrowserHlsSave";
    using (var hlsStorage = new JobStorage(Path.Combine(root, "HlsPipeline")))
    {
     var hlsScanner = new TrackingScanner(new DefenderScanner(runner));
     var hlsCoordinator = new DownloadCoordinator([new FixedCandidate(changing[0])], new SingleCandidate(), new AcquisitionPlanner(), [new HlsDownloadEngine(delayedHttp)],
      new MediaPipeline(tools, runner, new MediaProbe(tools, runner)), hlsScanner, hlsStorage);
     var hlsOutcome = await hlsCoordinator.RunAsync(new Uri(basis,"race").AbsoluteUri, Path.Combine(root,"HlsSaved"), new Progress<JobProgress>(), timeout.Token);
     browserHlsSave = hlsOutcome.Stage == JobStage.Completed && hlsScanner.Verdict == ScanVerdict.Clean && hlsOutcome.SavedPath is not null && !Directory.EnumerateDirectories(Path.Combine(hlsStorage.Root,"Jobs")).Any();
     if (browserHlsSave != true) throw new InvalidOperationException("Browser HLS normalize/scan/save/cleanup failed.");
    }
    using var proxy = new PublicNetworkProxy();
    checkpoint = "InvalidProxy";
    var brokenEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(root,"BrokenProxyBrowser"),
     new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--proxy-server={proxy.Address.AbsoluteUri} --proxy-bypass-list=<-loopback>" });
    browser = new BrowserWindow(); browser.Activate();
    try { await browser.VerifyPublicNavigationAsync(brokenEnvironment, timeout.Token); }
    catch (InvalidOperationException) when (proxy.RequestCount == 0) { invalidProxyReproduced = true; }
    if (invalidProxyReproduced != true) throw new InvalidOperationException("Original invalid proxy configuration did not reproduce.");
    browser.Close();
    var guardedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(root,"GuardedBrowser"),
     new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserProxyConfiguration.Arguments(proxy.Address) });
    browser = new BrowserWindow(); browser.Activate();
    checkpoint = "PublicBrowser";
    await browser.VerifyPublicNavigationAsync(guardedEnvironment, timeout.Token);
    if (proxy.RequestCount == 0) throw new InvalidOperationException("Public browser navigation bypassed the proxy.");
    publicBrowserNavigation = true;
    browser.Close();
    var beforeBlocked = proxy.RequestCount;
    checkpoint = "PrivateBrowser";
    browser = new BrowserWindow(); browser.Activate();
    var blocked = await browser.ResolveAsync(guardedEnvironment, basis, http, manifests, vault, timeout.Token, fixtureMode: true);
    if (blocked.Count != 0 || proxy.RequestCount <= beforeBlocked) throw new InvalidOperationException("Browser proxy did not actually handle and block the loopback fixture.");
    privateBrowserBlocked = true;
   }
   using var storage = new JobStorage(Path.Combine(root, "Pipeline"));
   checkpoint = "Pipeline";
   var scan = new TrackingScanner(new DefenderScanner(runner));
   var coordinator = new DownloadCoordinator([new DirectResolver(http)], new SingleCandidate(), new AcquisitionPlanner(), [new DirectDownloadEngine(http)],
    new MediaPipeline(tools, runner, new MediaProbe(tools, runner)), scan, storage);
   var outcome = await coordinator.RunAsync(new Uri(basis, "dynamic-media").AbsoluteUri, Path.Combine(root, "Saved"), new Progress<JobProgress>(), timeout.Token);
   var safeSave = scan.Verdict.HasValue && (scan.Verdict == ScanVerdict.Clean ? outcome.Stage == JobStage.Completed && outcome.SavedPath is not null : outcome.Stage == JobStage.Failed && outcome.SavedPath is null);
   if (!safeSave || Directory.EnumerateDirectories(Path.Combine(storage.Root, "Jobs")).Any()) throw new InvalidOperationException("Pipeline safe-save or cleanup failed.");
   server.Close(); await serving;
   await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Startup = true, Media = true, WebView2DynamicDiscovery = discovered, BrowserHlsDiscovery = hlsBrowserDiscovery, BrowserChangingCandidates = changingBrowserDiscovery, BrowserReplayHeaders = browserReplayHeaders, BrowserHlsSave = browserHlsSave, InvalidProxyReproduced = invalidProxyReproduced, BrowserPublicNavigation = publicBrowserNavigation, BrowserProxyBlocksPrivate = privateBrowserBlocked, BrowserSkippedElevated = elevated, MissingRuntimeHandled = missingHandled, Runtime = environment.BrowserVersionString,
    YtDlpVersion = ytVersion.Output.Trim(), SafeSave = safeSave, Defender = scan.Verdict.ToString(), PipelineOutcome = outcome.Stage.ToString(), TempCleanup = true }));
  }
  catch (Exception ex)
  {
   var frames = new System.Diagnostics.StackTrace(ex, true).GetFrames().Select(f => new { Type = f.GetMethod()?.DeclaringType?.Name, Method = f.GetMethod()?.Name, Line = f.GetFileLineNumber() }).ToArray();
   await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Startup = true, Success = false, Checkpoint = checkpoint, ErrorType = ex.GetType().Name, ErrorTarget = ex.TargetSite?.Name, Frames = frames, HResult = ex.HResult }));
  }
  finally { browser?.Close(); main.Close(); }
 }
 private sealed class SingleCandidate : ICandidateSelector
 {
  public Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates, CancellationToken ct) => Task.FromResult(candidates.Single());
 }
 private sealed class FixedCandidate(PlaybackCandidate candidate) : IResolver
 {
  public string Name => "Browser fixture";
  public Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlaybackCandidate>>([candidate]);
 }
 private sealed class TrackingScanner(IScanner scanner) : IScanner
 {
  public ScanVerdict? Verdict { get; private set; }
  public async Task<ScanVerdict> ScanAsync(string path, CancellationToken ct) { var result = await scanner.ScanAsync(path, ct); Verdict = result; return result; }
 }
 private static async Task ServeAsync(HttpListener listener, string video, CancellationToken ct)
 {
  try
  {
   while (listener.IsListening)
   {
    var context = await listener.GetContextAsync().WaitAsync(ct);
    if (context.Request.Url!.AbsolutePath is "/" or "/hls" or "/race")
    {
     var html = Encoding.UTF8.GetBytes(context.Request.Url.AbsolutePath == "/hls" ? "<!doctype html><video muted controls></video><script>setTimeout(()=>fetch('/dynamic.m3u8'),100)</script>" : "<!doctype html><video muted controls></video><script>setTimeout(()=>{document.querySelector('video').src='/dynamic-media';},100)</script>");
     if (context.Request.Url.AbsolutePath == "/race") html = Encoding.UTF8.GetBytes("<!doctype html><meta name='referrer' content='same-origin'><script>const opts={referrer:location.href,referrerPolicy:'same-origin'};fetch('/dynamic.m3u8',opts);let n=0;setInterval(()=>fetch('/late.m3u8?i='+n++,opts),200)</script>");
     context.Response.ContentType = "text/html"; context.Response.ContentLength64 = html.Length; await context.Response.OutputStream.WriteAsync(html, ct);
    }
    else if (context.Request.Url.AbsolutePath is "/dynamic.m3u8" or "/late.m3u8")
    {
     var bytes = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nsegment.ts\n#EXT-X-ENDLIST\n");
     context.Response.ContentType = "application/vnd.apple.mpegurl"; context.Response.ContentLength64 = bytes.Length;
     await context.Response.OutputStream.WriteAsync(bytes, ct);
    }
    else if (context.Request.Url.AbsolutePath is "/dynamic-media" or "/segment.ts")
    {
     var isSegment = context.Request.Url.AbsolutePath == "/segment.ts";
     var bytes = await File.ReadAllBytesAsync(isSegment ? Path.ChangeExtension(video,".ts") : video, ct); context.Response.ContentType = isSegment ? "video/mp2t" : "video/mp4"; context.Response.ContentLength64 = bytes.Length;
     await context.Response.OutputStream.WriteAsync(bytes, ct);
    }
    else context.Response.StatusCode = 404;
    context.Response.Close();
   }
  }
  catch (HttpListenerException) { }
  catch (ObjectDisposedException) { }
  catch (OperationCanceledException) { }
 }
 private sealed class DelayedManifestHandler : DelegatingHandler
 {
  public bool SawReplayHeaders { get; private set; }
  public string FailureReason { get; private set; } = "None";
  public DelayedManifestHandler() : base(new SocketsHttpHandler { UseProxy = false }) { }
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
  {
   if (request.Headers.Referrer?.AbsolutePath != "/race" || !request.Headers.UserAgent.ToString().Contains("Mozilla", StringComparison.Ordinal))
   {
    FailureReason = request.Headers.Referrer is null ? "RefererMissing" : request.Headers.Referrer.AbsolutePath != "/race" ? "RefererOriginOnly" : "UserAgent";
    throw new DownloadFailure(FailureCode.AccessRequired, "Fixture requires the original browser request headers.");
   }
   SawReplayHeaders = true;
   var response = await base.SendAsync(request, ct);
   try { await Task.Delay(500, ct); return response; }
   catch { response.Dispose(); throw; }
  }
 }
}
