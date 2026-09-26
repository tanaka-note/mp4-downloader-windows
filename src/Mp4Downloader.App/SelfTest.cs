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
  try
  {
   using var identity = WindowsIdentity.GetCurrent();
   var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
   if (elevated && !releaseSmoke) throw new InvalidOperationException("Browser diagnostics require a non-elevated user.");
   using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
   var tools = new ToolCatalog(Path.Combine(AppContext.BaseDirectory, "tools")); var runner = new ProcessRunner();
   var video = Path.Combine(root, "fixture.mp4");
   var created = await runner.RunAsync(tools.Find("ffmpeg"), ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=12", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", video], root, TimeSpan.FromSeconds(20), timeout.Token);
   if (created.ExitCode != 0) throw new InvalidOperationException("Fixture creation failed.");
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
   if (!elevated)
   {
    browser = new BrowserWindow(); browser.Activate();
    var candidates = await browser.ResolveAsync(environment, basis, http, manifests, vault, timeout.Token, fixtureMode: true);
    if (candidates.Count != 1 || candidates[0].Kind != MediaKind.Direct) throw new InvalidOperationException("Dynamic browser discovery failed.");
    discovered = true;
   }
   using var storage = new JobStorage(Path.Combine(root, "Pipeline"));
   var scan = new TrackingScanner(new DefenderScanner(runner));
   var coordinator = new DownloadCoordinator([new DirectResolver(http)], new SingleCandidate(), new AcquisitionPlanner(), [new DirectDownloadEngine(http)],
    new MediaPipeline(tools, runner, new MediaProbe(tools, runner)), scan, storage);
   var outcome = await coordinator.RunAsync(new Uri(basis, "dynamic-media").AbsoluteUri, Path.Combine(root, "Saved"), new Progress<JobProgress>(), timeout.Token);
   var safeSave = scan.Verdict.HasValue && (scan.Verdict == ScanVerdict.Clean ? outcome.Stage == JobStage.Completed && outcome.SavedPath is not null : outcome.Stage == JobStage.Failed && outcome.SavedPath is null);
   if (!safeSave || Directory.EnumerateDirectories(Path.Combine(storage.Root, "Jobs")).Any()) throw new InvalidOperationException("Pipeline safe-save or cleanup failed.");
   server.Close(); await serving;
   await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Startup = true, Media = true, WebView2DynamicDiscovery = discovered, BrowserSkippedElevated = elevated, MissingRuntimeHandled = missingHandled, Runtime = environment.BrowserVersionString,
    SafeSave = safeSave, Defender = scan.Verdict.ToString(), PipelineOutcome = outcome.Stage.ToString(), TempCleanup = true }));
  }
  catch (Exception ex)
  {
   await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Startup = true, Success = false, ErrorType = ex.GetType().Name, HResult = ex.HResult }));
  }
  finally { browser?.Close(); main.Close(); }
 }
 private sealed class SingleCandidate : ICandidateSelector
 {
  public Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates, CancellationToken ct) => Task.FromResult(candidates.Single());
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
    if (context.Request.Url!.AbsolutePath == "/")
    {
     var html = Encoding.UTF8.GetBytes("<!doctype html><video muted controls></video><script>setTimeout(()=>{document.querySelector('video').src='/dynamic-media';},100)</script>");
     context.Response.ContentType = "text/html"; context.Response.ContentLength64 = html.Length; await context.Response.OutputStream.WriteAsync(html, ct);
    }
    else if (context.Request.Url.AbsolutePath == "/dynamic-media")
    {
     var bytes = await File.ReadAllBytesAsync(video, ct); context.Response.ContentType = "video/mp4"; context.Response.ContentLength64 = bytes.Length;
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
}
