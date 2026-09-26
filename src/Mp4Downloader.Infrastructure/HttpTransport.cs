using System.Net;
using System.Net.Http.Headers;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class AuthContext
{
 public CookieContainer Cookies { get; } = new();
 public Dictionary<string, (Uri Origin, string Value)> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class AuthVault
{
 private readonly Dictionary<string, AuthContext> contexts = [];
 public string Add(AuthContext context) { var id = Guid.NewGuid().ToString("N"); contexts[id] = context; return id; }
 public AuthContext? Get(string? id) => id is not null && contexts.TryGetValue(id, out var value) ? value : null;
 public void Clear() => contexts.Clear();
}
public sealed class HttpTransport : IDisposable
{
 private readonly HttpClient client;
 private readonly AuthVault vault;
 public HttpTransport(AuthVault? vault = null, HttpMessageHandler? handler = null, bool allowPrivateNetwork = false)
 {
  this.vault = vault ?? new();
  var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) };
  if (!allowPrivateNetwork) sockets.ConnectCallback = NetworkPolicy.ConnectPublicAsync;
  client = new(handler ?? sockets);
  client.Timeout = Timeout.InfiniteTimeSpan;
 }
 public async Task<HttpResponseMessage> SendAsync(Uri uri, string? authId, RangeHeaderValue? range, string? ifRange, CancellationToken ct)
 {
  uri = UrlPolicy.Parse(uri.AbsoluteUri);
  var initial = uri;
  for (var redirects = 0; redirects <= 8; redirects++)
  {
   using var request = new HttpRequestMessage(HttpMethod.Get, uri);
   request.Headers.UserAgent.ParseAdd("Mp4Downloader/0.2");
   request.Headers.AcceptEncoding.ParseAdd("identity");
   if (range is not null) request.Headers.Range = range;
   if (ifRange is not null) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
   var auth = vault.Get(authId);
   if (auth is not null && !(initial.Scheme == "https" && uri.Scheme == "http"))
   {
    var cookies = auth.Cookies.GetCookieHeader(uri);
    if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
    foreach (var (key, value) in auth.Headers)
    {
     var name = key.Split('|')[0];
     if (UrlPolicy.SameOrigin(uri, value.Origin) && name is "Authorization" or "Referer" or "User-Agent")
     { request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value.Value); }
    }
   }
   using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
   timeout.CancelAfter(TimeSpan.FromSeconds(30));
   HttpResponseMessage response;
   try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
   catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DownloadFailure(FailureCode.Network, "通信がタイムアウトしました。"); }
   catch (HttpRequestException) { throw new DownloadFailure(FailureCode.Network, "動画サーバーへ接続できませんでした。"); }
   if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
   {
    var location = response.Headers.Location;
    response.Dispose();
    if (location is null) throw new DownloadFailure(FailureCode.Network, "転送先を確認できませんでした。");
    uri = UrlPolicy.Parse(new Uri(uri, location).AbsoluteUri);
    continue;
   }
   return response;
  }
  throw new DownloadFailure(FailureCode.Network, "転送回数が上限を超えました。");
 }
 public async Task<(byte[] Data, Uri Actual, string Mime)> ReadAsync(Uri uri, string? authId, int limit, CancellationToken ct)
 {
  using var response = await SendAsync(uri, authId, null, null, ct);
  RequireSuccess(response);
  if (response.Content.Headers.ContentLength > limit) throw new DownloadFailure(FailureCode.Unsupported, "解析データが上限を超えています。");
  await using var input = await response.Content.ReadAsStreamAsync(ct);
  using var output = new MemoryStream();
  var buffer = new byte[8192];
  while (true)
  {
   var count = await ReadWithTimeoutAsync(input, buffer, ct);
   if (count == 0) break;
   if (output.Length + count > limit) throw new DownloadFailure(FailureCode.Unsupported, "解析データが上限を超えています。");
   output.Write(buffer, 0, count);
  }
  return (output.ToArray(), response.RequestMessage?.RequestUri ?? uri, response.Content.Headers.ContentType?.MediaType ?? "");
 }
 public static async Task<int> ReadWithTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
 {
  using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
  timeout.CancelAfter(TimeSpan.FromSeconds(30));
  try { return await input.ReadAsync(buffer, timeout.Token); }
  catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DownloadFailure(FailureCode.Network, "通信がタイムアウトしました。"); }
 }
 public static void RequireSuccess(HttpResponseMessage response)
 {
  if (response.IsSuccessStatusCode) return;
  throw new DownloadFailure(response.StatusCode switch {
   HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FailureCode.AccessRequired,
   HttpStatusCode.TooManyRequests => FailureCode.RateLimited,
   HttpStatusCode.NotFound => FailureCode.NoMatch,
   _ => FailureCode.Network
  }, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "動画へのアクセス権または再認証が必要です。" : "動画サーバーが正常に応答しませんでした。");
 }
 public void Dispose() => client.Dispose();
}
