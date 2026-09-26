using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class ManifestResolver(HttpTransport http) : IResolver
{
 public string Name => "Manifest";
 public Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct) => ResolveAsync(page, page, null, ct);
 public async Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri url, Uri page, string? auth, CancellationToken ct)
 {
  var result = await http.ReadAsync(url, auth, 2 * 1024 * 1024, ct);
  var text = Encoding.UTF8.GetString(result.Data).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
  if (text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return [await HlsAsync(text, result.Actual, page, auth, 0, ct)];
  if (text.Contains("<MPD", StringComparison.Ordinal) || result.Mime == "application/dash+xml") return [Dash(text, result.Actual, page, auth)];
  return [];
 }
 private async Task<PlaybackCandidate> HlsAsync(string text, Uri url, Uri page, string? auth, int depth, CancellationToken ct)
 {
  if (depth > 5) throw new DownloadFailure(FailureCode.Unsupported, "Manifestの入れ子が上限を超えています。");
  var lines = Lines(text);
  if (lines.Any(l => l.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal) && !l.Contains("METHOD=NONE", StringComparison.Ordinal)))
   return Blocked(page, url, MediaKind.Hls, text, HlsProtection(text), auth);
  var variants = new List<(Dictionary<string, string> Attr, Uri Url)>();
  for (var i = 0; i + 1 < lines.Length; i++)
   if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) && !lines[i + 1].StartsWith('#'))
    variants.Add((Attributes(lines[i]), Resolve(url, lines[i + 1])));
  if (variants.Count > 0)
  {
   var selected = variants.OrderByDescending(v => long.TryParse(v.Attr.GetValueOrDefault("BANDWIDTH"), out var n) ? n : 0).First();
   var video = await http.ReadAsync(selected.Url, auth, 2 * 1024 * 1024, ct);
   var candidate = await HlsAsync(Encoding.UTF8.GetString(video.Data), video.Actual, page, auth, depth + 1, ct);
   var tracks = candidate.Tracks.ToList();
   if (selected.Attr.TryGetValue("AUDIO", out var group))
   {
    var audio = lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)).Select(Attributes)
     .Where(a => a.GetValueOrDefault("TYPE") == "AUDIO" && a.GetValueOrDefault("GROUP-ID") == group && a.ContainsKey("URI"))
     .OrderByDescending(a => a.GetValueOrDefault("DEFAULT") == "YES").ThenByDescending(a => a.GetValueOrDefault("LANGUAGE") == "ja").FirstOrDefault();
    if (audio is not null)
    {
     var audioUrl = Resolve(url, audio["URI"]);
     var loaded = await http.ReadAsync(audioUrl, auth, 2 * 1024 * 1024, ct);
     var audioText = Encoding.UTF8.GetString(loaded.Data);
     var protection = HlsProtection(audioText);
     if (protection != Protection.Clear) return Blocked(page, audioUrl, MediaKind.Hls, audioText, protection, auth);
     ValidateVod(audioText);
     tracks.Add(new("audio", loaded.Actual, "audio", Language: audio.GetValueOrDefault("LANGUAGE"), Manifest: AbsoluteHls(audioText, loaded.Actual)));
    }
   }
   return candidate with { Tracks = tracks, Evidence = "HLS master video/audio pairing" };
  }
  var protectedState = HlsProtection(text);
  if (protectedState != Protection.Clear) return Blocked(page, url, MediaKind.Hls, text, protectedState, auth);
  ValidateVod(text);
  var duration = lines.Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).Sum(l => double.Parse(l[8..].Split(',')[0], CultureInfo.InvariantCulture));
  if (!double.IsFinite(duration) || duration <= 0) throw new DownloadFailure(FailureCode.Corrupt, "Manifestの再生時間が不正です。");
  return new("hls:" + page.GetLeftPart(UriPartial.Path), "動画", page, MediaKind.Hls,
   [new("video", url, "video", Manifest: AbsoluteHls(text, url))], Protection.Clear, "Complete HLS VOD playlist", true, duration, AuthContextId: auth);
 }
 public static Protection HlsProtection(string text)
 {
  foreach (var line in Lines(text).Where(l => l.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) || l.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)))
  {
   var attrs = Attributes(line);
   if (attrs.GetValueOrDefault("METHOD") == "NONE") continue;
   var keyFormat = attrs.GetValueOrDefault("KEYFORMAT");
   if (keyFormat is not null && keyFormat != "identity") return Protection.Drm;
   return Protection.Encrypted;
  }
  return Protection.Clear;
 }
 public static void ValidateVod(string text)
 {
  var lines = Lines(text);
  if (!lines.Contains("#EXT-X-ENDLIST") || lines.Any(l => l.StartsWith("#EXT-X-PART", StringComparison.Ordinal) || l.StartsWith("#EXT-X-PRELOAD-HINT", StringComparison.Ordinal)))
   throw new DownloadFailure(FailureCode.Unsupported, "LiveまたはLL-HLSには対応していません。");
  if (!lines.Any(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))) throw new DownloadFailure(FailureCode.Corrupt, "Manifestに動画segmentがありません。");
  if (lines.Count(l => !l.StartsWith('#')) > 10000) throw new DownloadFailure(FailureCode.Unsupported, "動画segment数が上限を超えています。");
 }
 public static string AbsoluteHls(string text, Uri basis)
 {
  return string.Join('\n', Lines(text).Select(line => !line.StartsWith('#') ? Resolve(basis, line).AbsoluteUri : Regex.Replace(line, "URI=\"([^\"]+)\"", m => "URI=\"" + Resolve(basis, m.Groups[1].Value).AbsoluteUri + "\""))) + "\n";
 }
 public static PlaybackCandidate Dash(string text, Uri url, Uri page, string? auth)
 {
  using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
  XDocument document;
  try { document = XDocument.Load(reader); }
  catch (XmlException) { throw new DownloadFailure(FailureCode.Corrupt, "DASH Manifestを読み取れませんでした。"); }
  var root = document.Root;
  if (root?.Name.LocalName != "MPD") throw new DownloadFailure(FailureCode.NoMatch, "DASH Manifestではありません。");
  if (root.Descendants().Any(e => e.Name.LocalName == "ContentProtection")) return Blocked(page, url, MediaKind.Dash, text, Protection.Drm, auth);
  if ((string?)root.Attribute("type") == "dynamic" || root.Descendants().Count(e => e.Name.LocalName == "Period") != 1 || root.Descendants().Any(e => e.Attributes().Any(a => a.Name.NamespaceName.Contains("xlink", StringComparison.Ordinal))))
   throw new DownloadFailure(FailureCode.Unsupported, "この動的または複雑なDASHには対応していません。");
  var bases = root.Elements().Where(e => e.Name.LocalName == "BaseURL").ToList();
  if (bases.Count == 0) root.AddFirst(new XElement(root.Name.Namespace + "BaseURL", new Uri(url, ".").AbsoluteUri));
  else foreach (var basis in bases) basis.Value = Resolve(url, basis.Value).AbsoluteUri;
  foreach (var attr in root.Descendants().Attributes().Where(a => a.Name.LocalName is "media" or "initialization" or "sourceURL"))
   if (Uri.TryCreate(attr.Value, UriKind.Absolute, out var absolute)) _ = UrlPolicy.Parse(absolute.AbsoluteUri);
  foreach (var basis in root.Descendants().Where(e => e.Name.LocalName == "BaseURL"))
   if (Uri.TryCreate(basis.Value, UriKind.Absolute, out var absolute)) _ = UrlPolicy.Parse(absolute.AbsoluteUri);
  return new("dash:" + page.GetLeftPart(UriPartial.Path), "動画", page, MediaKind.Dash,
   [new("manifest", url, "manifest")], Protection.Clear, "Static clear single-period DASH", true, AuthContextId: auth, Snapshot: document.ToString(SaveOptions.DisableFormatting));
 }
 private static PlaybackCandidate Blocked(Uri page, Uri url, MediaKind kind, string text, Protection protection, string? auth) =>
  new("protected", "動画", page, kind, [new("manifest", url, "manifest")], protection, "Manifest protection declaration", true, AuthContextId: auth, Snapshot: text);
 public static string[] Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
 public static Dictionary<string, string> Attributes(string line) => Regex.Matches(line[(line.IndexOf(':') + 1)..], "([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,]*))")
  .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, StringComparer.Ordinal);
 private static Uri Resolve(Uri basis, string relative) => UrlPolicy.Parse(new Uri(basis, relative).AbsoluteUri);
}
