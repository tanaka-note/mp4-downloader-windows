using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using System.Net;
using System.Text.Json;

namespace Mp4Downloader.App;

public sealed class BrowserResolver(Window owner, HttpTransport http, ManifestResolver manifests, AuthVault vault, string profile, Uri proxy) : IResolver
{
 public string Name => "WebView2";
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct)
 {
  _ = await NetworkPolicy.PublicAddressesAsync(page.IdnHost,ct);
  CoreWebView2Environment environment;
  try { environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserProxyConfiguration.Arguments(proxy) }); }
  catch (Exception ex) { throw BrowserFailureDetails.From(BrowserPhase.Initialization, ex); }
  var window = new BrowserWindow();
  window.Activate();
  try { return await window.ResolveAsync(environment, page, http, manifests, vault, ct); }
  catch (OperationCanceledException) { throw; }
  catch (DownloadFailure) { throw; }
  catch (Exception ex) { throw BrowserFailureDetails.From(window.Phase, ex); }
  finally { window.Close(); owner.Activate(); }
 }
}

public sealed class BrowserWindow : Window
{
 internal BrowserPhase Phase { get; private set; } = BrowserPhase.Initialization;
 internal async Task VerifyPublicNavigationAsync(CoreWebView2Environment environment, CancellationToken ct)
 {
  await browser.EnsureCoreWebView2Async(environment);
  var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
  void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
  {
   if (e.IsSuccess && e.HttpStatusCode == 200) loaded.TrySetResult(true);
   else loaded.TrySetException(new InvalidOperationException("Public browser navigation failed: " + e.WebErrorStatus));
  }
  browser.CoreWebView2.NavigationCompleted += Completed;
  try
  {
   browser.CoreWebView2.Navigate("https://example.com/");
   await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
   var title = await browser.CoreWebView2.ExecuteScriptAsync("document.title");
   if (title != "\"Example Domain\"") throw new InvalidOperationException("Public browser navigation did not load the expected document.");
  }
  finally { browser.CoreWebView2.NavigationCompleted -= Completed; }
 }
 private readonly WebView2 browser = new();
 private readonly TextBlock host = new() { TextWrapping = TextWrapping.Wrap };
 private readonly TextBlock notice = new() { Text = "必要ならログインして動画を再生し、「動画を確認」を押してください。", TextWrapping = TextWrapping.Wrap };
 private readonly Button done = new() { Content = "動画を確認" };
 private readonly Dictionary<Uri, string> observed = [];
 private readonly Dictionary<Uri, Dictionary<string, string>> replayHeaders = [];
 internal int CapturedRefererCount => replayHeaders.Values.Count(h => h.ContainsKey("Referer"));
 public BrowserWindow()
 {
  Title = "MP4 Downloader — 動画ページ";
  var grid = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
  grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
  grid.Children.Add(host); Grid.SetRow(notice, 1); grid.Children.Add(notice); Grid.SetRow(done, 2); grid.Children.Add(done); Grid.SetRow(browser, 3); grid.Children.Add(browser); Content = grid;
  AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));
  Closed += (_, _) => browser.Close();
 }
 public async Task ClearAsync(CoreWebView2Environment environment)
 {
  Activate(); await browser.EnsureCoreWebView2Async(environment);
  await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
  browser.Close(); Close();
 }
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(CoreWebView2Environment environment, Uri page, HttpTransport http, ManifestResolver manifests, AuthVault vault, CancellationToken ct, bool fixtureMode = false)
 {
  await browser.EnsureCoreWebView2Async(environment);
  var core = browser.CoreWebView2;
  core.Settings.IsPasswordAutosaveEnabled = false;
  core.Settings.IsGeneralAutofillEnabled = false;
  core.Settings.AreHostObjectsAllowed = false;
  core.Settings.IsWebMessageEnabled = false;
  core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
  if (!fixtureMode)
  {
   core.WebResourceRequested += async (sender, e) =>
   {
    var deferral = e.GetDeferral();
    try
    {
     var uri = UrlPolicy.Parse(e.Request.Uri);
     using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
     _ = await NetworkPolicy.PublicAddressesAsync(uri.IdnHost,deadline.Token);
    }
    catch (Exception)
    {
     try { e.Response = environment.CreateWebResourceResponse(null,403,"Local network access blocked","Content-Type: text/plain"); }
     catch (Exception) { /* Browser may already be closing. */ }
    }
    finally { try { deferral.Complete(); } catch (Exception) { /* Browser may already be closed. */ } }
   };
  }
  core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
  core.DownloadStarting += (_, e) => e.Cancel = true;
  core.NewWindowRequested += (_, e) => { e.Handled = true; notice.Text = "新しいウィンドウを要求するページはこの解析画面では開けません。"; };
  core.NavigationStarting += (_, e) =>
  {
   if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { e.Cancel = true; return; }
   host.Text = "アクセス先: " + uri.IdnHost;
   // Browser interactions can navigate across origins for normal login; no native API or secrets are injected.
  };
  core.WebResourceResponseReceived += (_, e) =>
  {
   if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
   string mime;
   try { mime = e.Response.Headers.GetHeader("Content-Type"); } catch (ArgumentException) { mime = ""; }
   var path = uri.AbsolutePath.ToLowerInvariant();
   if (!(mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".m3u8") || path.EndsWith(".mpd"))) return;
   if (path.EndsWith(".ts") || path.EndsWith(".m4s") || observed.Count >= 100) return;
   if (e.Response.StatusCode is < 200 or >= 300) return;
   observed[uri] = mime;
   var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
   foreach (var header in e.Request.Headers)
   {
    var name = new[] { "Authorization", "Referer", "User-Agent" }.FirstOrDefault(n => n.Equals(header.Key, StringComparison.OrdinalIgnoreCase));
    if (name is not null && !string.IsNullOrEmpty(header.Value)) headers[name] = header.Value;
   }
   replayHeaders[uri] = headers;
  };
  var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
  core.NavigationCompleted += (_, e) =>
  {
   if (!e.IsSuccess && !fixtureMode)
   {
    notice.Text = "ページへの接続に失敗しました（" + e.WebErrorStatus + "）。";
    ready.TrySetException(new DownloadFailure(FailureCode.Network, notice.Text));
   }
  };
  done.Click += (_, _) => ready.TrySetResult(true);
  Closed += (_, _) => ready.TrySetCanceled();
  using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => { ready.TrySetCanceled(ct); browser.Close(); }));
  Phase = BrowserPhase.Navigation;
  core.Navigate(page.AbsoluteUri);
  if (fixtureMode)
  {
   if (!IPAddress.TryParse(page.Host, out var address) || !IPAddress.IsLoopback(address)) throw new InvalidOperationException("Self-test is loopback-only.");
   await Task.Delay(2500, ct);
   ready.TrySetResult(true);
  }
  await ready.Task.WaitAsync(ct);
  done.IsEnabled = false;
  notice.Text = "検出した動画候補を解析しています。";
  Phase = BrowserPhase.Dom;
  var script = "JSON.stringify(Array.from(document.querySelectorAll('video')).map(v=>({src:v.currentSrc||v.src,width:v.videoWidth,height:v.videoHeight,duration:Number.isFinite(v.duration)?v.duration:null})))";
  var result = await core.ExecuteScriptAsync(script);
  var players = new Dictionary<Uri, (int Width, int Height, double? Duration)>();
  var dom = JsonSerializer.Deserialize<string>(result);
  if (dom is not null)
  {
   using var json = JsonDocument.Parse(dom);
   foreach (var player in json.RootElement.EnumerateArray())
    if (Uri.TryCreate(player.GetProperty("src").GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
    { observed.TryAdd(uri, ""); players[uri] = (player.GetProperty("width").GetInt32(), player.GetProperty("height").GetInt32(), player.GetProperty("duration").TryGetDouble(out var duration) ? duration : null); }
  }
  var candidates = new List<PlaybackCandidate>();
  DownloadFailure? candidateFailure = null;
  // Network events continue while cookies and manifests are awaited. Enumerate a
  // fixed batch, never the live dictionary that those events keep extending.
  var batch = observed.Select(item => (Url: item.Key, Mime: item.Value,
   Headers: replayHeaders.GetValueOrDefault(item.Key)?.ToArray() ?? [])).ToArray();
  foreach (var (url, mime, headers) in batch)
  {
   ct.ThrowIfCancellationRequested();
   Phase = BrowserPhase.Cookies;
   var context = new AuthContext();
   foreach (var cookie in await core.CookieManager.GetCookiesAsync(url.AbsoluteUri))
   {
    try
    {
     var mapped = new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain) { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly };
     if (!cookie.IsSession) mapped.Expires = DateTime.UnixEpoch.AddSeconds(cookie.Expires);
     context.Cookies.Add(mapped);
    }
    catch (CookieException) { }
   }
   foreach (var header in headers) context.Headers[header.Key] = (url, header.Value);
   if (!context.Headers.ContainsKey("User-Agent")) context.Headers["User-Agent"] = (url, core.Settings.UserAgent);
   var authId = context.Cookies.Count > 0 || context.Headers.Count > 0 ? vault.Add(context) : null;
   Phase = BrowserPhase.Candidates;
   try
   {
    if (url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase))
    { candidates.AddRange(await manifests.ResolveAsync(url, page, authId, ct)); continue; }
    if (!players.TryGetValue(url, out var player)) continue; // A response alone is insufficient evidence of the main video.
    var probe = await DirectResolver.ProbeAsync(http, url, authId, ct);
    if (!probe.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) continue;
    candidates.Add(new("browser:" + candidates.Count, "動画", page, MediaKind.Direct, [new("video", probe.Url, "video")], Protection.Clear,
     "Current video element and media response", true, player.Duration, player.Width, player.Height, authId));
   }
   catch (DownloadFailure ex) when (ex.CanFallback || ex.Code == FailureCode.AccessRequired) { candidateFailure = ex; }
  }
  browser.Close();
  if (candidates.Count == 0 && !fixtureMode)
   throw candidateFailure ?? new DownloadFailure(FailureCode.NoMatch, "ページは開けましたが、取得できる動画候補を検出できませんでした。動画を再生してから、もう一度確認してください。");
  return candidates;
 }
}
