using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class HtmlResolver(HttpTransport http, ManifestResolver manifests) : IResolver
{
 public string Name => "HTML";
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct)
 {
  var result = await http.ReadAsync(page, null, 2 * 1024 * 1024, ct);
  if (!result.Mime.Contains("html", StringComparison.OrdinalIgnoreCase)) return [];
  var html = Encoding.UTF8.GetString(result.Data);
  var candidates = new List<PlaybackCandidate>();
  // Scope sources to video elements; do not scrape arbitrary JavaScript URLs or iframe ads.
  foreach (Match video in Regex.Matches(html, "<video\\b[^>]*>(?:[\\s\\S]*?</video>)?", RegexOptions.IgnoreCase))
   foreach (Match source in Regex.Matches(video.Value, "\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase))
   {
    if (!Uri.TryCreate(result.Actual, WebUtility.HtmlDecode(source.Groups[1].Value), out var url) || url.Scheme is not ("http" or "https")) continue;
    if (url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
    { candidates.AddRange(await manifests.ResolveAsync(url, page, null, ct)); continue; }
    var probe = await DirectResolver.ProbeAsync(http, url, null, ct);
    if (!probe.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) continue;
    candidates.Add(new("html:" + candidates.Count, "動画", page, MediaKind.Direct, [new("video", probe.Url, "video")], Protection.Clear, "HTML video element and media MIME", true));
   }
  return candidates.DistinctBy(c => c.Tracks[0].Url).ToArray();
 }
}
