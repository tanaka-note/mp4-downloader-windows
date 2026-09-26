using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using System.Net;
using System.Text.Json;

namespace Mp4Downloader.App;

public sealed class BrowserResolver(Window owner, HttpTransport http, ManifestResolver manifests, AuthVault vault, string profile) : IResolver
{
 public string Name => "WebView2";
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct)
 {
  CoreWebView2Environment environment;
  try { environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null); }
  catch (Exception) { throw new DownloadFailure(FailureCode.Tool, "WebView2 Runtimeが利用できません。設定のMicrosoft公式導入案内を確認してください。"); }
  var window = new BrowserWindow();
  window.Activate();
  try { return await window.ResolveAsync(environment, page, http, manifests, vault, ct); }
  finally { window.Close(); owner.Activate(); }
 }
}

public sealed class BrowserWindow : Window
{
 private readonly WebView2 browser = new();
 private readonly TextBlock host = new() { TextWrapping = TextWrapping.Wrap };
 private readonly TextBlock notice = new() { Text = "必要ならログインして動画を再生し、「動画を確認」を押してください。", TextWrapping = TextWrapping.Wrap };
 private readonly Button done = new() { Content = "動画を確認" };
 private readonly Dictionary<Uri, string> observed = [];
 private readonly Dictionary<Uri, string> authorizations = [];
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
   observed[uri] = mime;
   try { var auth = e.Request.Headers.GetHeader("Authorization"); if (!string.IsNullOrEmpty(auth)) authorizations[uri] = auth; } catch (ArgumentException) { }
  };
  var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
  done.Click += (_, _) => ready.TrySetResult(true);
  Closed += (_, _) => ready.TrySetCanceled();
  using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => { ready.TrySetCanceled(ct); browser.Close(); }));
  core.Navigate(page.AbsoluteUri);
  if (fixtureMode)
  {
   if (!IPAddress.TryParse(page.Host, out var address) || !IPAddress.IsLoopback(address)) throw new InvalidOperationException("Self-test is loopback-only.");
   await Task.Delay(2500, ct);
   ready.TrySetResult(true);
  }
  await ready.Task.WaitAsync(ct);
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
  foreach (var (url, mime) in observed)
  {
   ct.ThrowIfCancellationRequested();
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
   if (authorizations.TryGetValue(url, out var authorization)) context.Headers["Authorization"] = (url, authorization);
   var authId = context.Cookies.Count > 0 || context.Headers.Count > 0 ? vault.Add(context) : null;
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
   catch (DownloadFailure ex) when (ex.CanFallback || ex.Code == FailureCode.AccessRequired) { }
  }
  browser.Close();
  return candidates;
 }
}
