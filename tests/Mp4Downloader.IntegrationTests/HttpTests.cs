using System.Net;
using System.Net.Http.Headers;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using Xunit;

namespace Mp4Downloader.IntegrationTests;

public class HttpTests
{
 [Theory]
 [InlineData("valid")]
 [InlineData("no-range")]
 [InlineData("wrong-range")]
 [InlineData("changed-etag")]
 [InlineData("short-chunk")]
 [InlineData("weak-etag")]
 public async Task RangeFailureFallsBackWithoutMixingChunks(string mode)
 {
  var content = Enumerable.Range(0, 131072).Select(i => (byte)(i % 251)).ToArray();
  var handler = new MediaHandler(content, mode);
  using var transport = new HttpTransport(handler: handler);
  var root = Temp();
  try
  {
   var url = new Uri("https://fixture.invalid/video");
   var candidate = new PlaybackCandidate("fixture", "fixture", url, MediaKind.Direct, [new("video", url, "video")], Protection.Clear, "fixture", true);
   var result = await new DirectDownloadEngine(transport, 1024).DownloadAsync(new(candidate, "DirectHttp"), root, new Progress<JobProgress>(), default);
   Assert.True(result.Complete); Assert.Equal(content, await File.ReadAllBytesAsync(result.Files[0]));
   Assert.Empty(Directory.EnumerateFiles(root, "*.part"));
   if (mode is "wrong-range" or "changed-etag" or "short-chunk") Assert.True(handler.FullRequests > 0);
   if (mode is "no-range" or "weak-etag") Assert.Equal(0, handler.ChunkRequests);
  }
  finally { Directory.Delete(root, true); }
 }
 [Fact] public async Task CrossOriginRedirectDoesNotForwardAuthorizationOrOriginCookies()
 {
  var auth = new AuthContext();
  auth.Cookies.Add(new Cookie("fixture", "nonsecret", "/", "a.invalid"));
  auth.Headers["Authorization"] = (new("https://a.invalid"), "Bearer fixture-nonsecret");
  var vault = new AuthVault(); var id = vault.Add(auth);
  var handler = new RedirectHandler("https://b.invalid/video");
  using var transport = new HttpTransport(vault, handler);
  using var response = await transport.SendAsync(new("https://a.invalid/video"), id, null, null, default);
  Assert.True(handler.FirstAuth); Assert.False(handler.FinalAuth); Assert.False(handler.FinalCookie);
 }
 [Fact] public async Task SameOriginRedirectKeepsApplicableHeaders()
 {
  var auth = new AuthContext(); auth.Headers["Authorization"] = (new("https://a.invalid"), "Bearer fixture-nonsecret");
  var vault = new AuthVault(); var id = vault.Add(auth); var handler = new RedirectHandler("https://a.invalid/other");
  using var transport = new HttpTransport(vault, handler);
  using var response = await transport.SendAsync(new("https://a.invalid/video"), id, null, null, default);
  Assert.True(handler.FinalAuth);
 }
 [Fact] public async Task HttpsDowngradeSendsNoSecrets()
 {
  var auth = new AuthContext(); auth.Cookies.Add(new Cookie("fixture", "nonsecret", "/", "a.invalid"));
  var vault = new AuthVault(); var id = vault.Add(auth); var handler = new RedirectHandler("http://a.invalid/video");
  using var transport = new HttpTransport(vault, handler);
  using var response = await transport.SendAsync(new("https://a.invalid/video"), id, null, null, default);
  Assert.False(handler.FinalCookie);
 }
 [Theory]
 [InlineData(403, FailureCode.AccessRequired)]
 [InlineData(404, FailureCode.NoMatch)]
 [InlineData(429, FailureCode.RateLimited)]
 [InlineData(500, FailureCode.Network)]
 public void StatusIsClassified(int status, FailureCode expected) => Assert.Equal(expected, Assert.Throws<DownloadFailure>(() => HttpTransport.RequireSuccess(new((HttpStatusCode)status))).Code);
 [Fact] public async Task ContentTypeFindsExtensionlessVideo()
 {
  using var http = new HttpTransport(handler: new MediaHandler(new byte[4096], "valid"));
  var result = await new DirectResolver(http).ResolveAsync(new("https://fixture.invalid/resource"), default);
  Assert.Single(result); Assert.Equal(MediaKind.Direct, result[0].Kind);
 }
 [Fact] public async Task CancellationStopsBodyReadAndClosesPartial()
 {
  using var http = new HttpTransport(handler: new MediaHandler(new byte[4096], "valid"));
  using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
  var root = Temp();
  try
  {
   var url = new Uri("https://fixture.invalid/v");
   var candidate = new PlaybackCandidate("fixture", "fixture", url, MediaKind.Direct, [new("v", url, "video")], Protection.Clear, "fixture", true);
   await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DirectDownloadEngine(http).DownloadAsync(new(candidate, "DirectHttp"), root, new Progress<JobProgress>(), cancellation.Token));
  }
  finally { Directory.Delete(root, true); }
 }
 private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "mp4-http-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
 private sealed class MediaHandler(byte[] data, string mode) : HttpMessageHandler
 {
  public int FullRequests, ChunkRequests;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   var range = request.Headers.Range?.Ranges.First();
   var probe = range?.From == 0 && range.To == 65535;
   var partial = range is not null && mode != "no-range";
   if (!probe && range is not null) Interlocked.Increment(ref ChunkRequests);
   if (range is null) Interlocked.Increment(ref FullRequests);
   var from = partial ? range!.From!.Value : 0;
   var to = partial ? Math.Min(range!.To!.Value, data.Length - 1) : data.Length - 1;
   var length = (int)(to - from + 1);
   if (mode == "short-chunk" && partial && !probe) length--;
   var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(data, (int)from, length) };
   response.Content.Headers.ContentType = new("video/mp4");
   response.Headers.ETag = new(mode == "changed-etag" && !probe && partial ? "\"changed\"" : "\"fixture\"", mode == "weak-etag");
   if (partial) response.Content.Headers.ContentRange = new(mode == "wrong-range" && !probe ? from + 1 : from, to, data.Length);
   return Task.FromResult(response);
  }
 }
 private sealed class RedirectHandler(string destination) : HttpMessageHandler
 {
  private int calls; public bool FirstAuth, FinalAuth, FinalCookie;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
  {
   if (calls++ == 0) { FirstAuth = request.Headers.Contains("Authorization"); var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(destination); return Task.FromResult(response); }
   FinalAuth = request.Headers.Contains("Authorization"); FinalCookie = request.Headers.Contains("Cookie");
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]), RequestMessage = request });
  }
 }
}
