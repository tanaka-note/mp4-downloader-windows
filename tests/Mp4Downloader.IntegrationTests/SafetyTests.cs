using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using Xunit;
namespace Mp4Downloader.IntegrationTests;

public class SafetyTests
{
 [Theory]
 [InlineData("CON", "_CON")]
 [InlineData("LPT1.mp4", "_LPT1.mp4")]
 [InlineData("video. ", "video")]
 [InlineData("a/b\\c:d", "a_b_c_d")]
 [InlineData("", "video")]
 [InlineData("日本語動画", "日本語動画")]
 public void SanitizesFilename(string input, string expected) => Assert.Equal(expected, SafeFileName.Base(input));
 [Theory]
 [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"key\"", Protection.Encrypted)]
 [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT=\"com.apple.streamingkeydelivery\"", Protection.Drm)]
 [InlineData("#EXT-X-KEY:METHOD=NONE", Protection.Clear)]
 public void ClassifiesHlsProtection(string line, Protection expected) => Assert.Equal(expected, ManifestResolver.HlsProtection("#EXTM3U\n" + line));
 [Fact] public void RejectsLive() => Assert.Throws<DownloadFailure>(() => ManifestResolver.ValidateVod("#EXTM3U\n#EXTINF:2,\nsegment.ts"));
 [Fact] public void DashRejectsExternalEntities() => Assert.Throws<DownloadFailure>(() => ManifestResolver.Dash("<!DOCTYPE MPD [<!ENTITY x SYSTEM 'file:///C:/fixture'>]><MPD>&x;</MPD>", new("https://fixture.invalid/a.mpd"), new("https://fixture.invalid/"), null));
 [Fact] public void DashDetectsDrm()
 {
  var candidate = ManifestResolver.Dash("<MPD><Period><AdaptationSet><ContentProtection schemeIdUri='urn:mpeg:dash:mp4protection:2011'/></AdaptationSet></Period></MPD>", new("https://fixture.invalid/a.mpd"), new("https://fixture.invalid/"), null);
  Assert.Equal(Protection.Drm, candidate.Protection);
 }
 [Fact] public void RelativeSegmentsResolveWithoutCopyingManifestQuery()
 {
  var text = ManifestResolver.AbsoluteHls("#EXTM3U\n#EXTINF:2,\nsegment.ts\n#EXT-X-ENDLIST", new("https://fixture.invalid/dir/a.m3u8?fixture=private"));
  Assert.Contains("https://fixture.invalid/dir/segment.ts", text); Assert.DoesNotContain("fixture=private", text);
 }
 [Fact] public void SignedHlsStaysInMemoryAndSignedDashIsRejected()
 {
  var page = new Uri("https://fixture.invalid/watch");
  var hls = new PlaybackCandidate("fixture", "fixture", page, MediaKind.Hls, [new("v", new("https://fixture.invalid/a.m3u8"), "video", Manifest: "#EXTM3U\nhttps://fixture.invalid/segment.ts?sig=fixture\n")], Protection.Clear, "fixture", true);
  Assert.Equal("HlsHttp", Assert.Single(new AcquisitionPlanner().Plan(hls)).Engine);
  var dash = hls with { Kind = MediaKind.Dash, Snapshot = "<MPD><SegmentTemplate media=\"segment.m4s?sig=fixture\"/></MPD>" };
  Assert.Throws<DownloadFailure>(() => new AcquisitionPlanner().Plan(dash));
 }
 [Theory]
 [InlineData(0, "Scan finished.\nScanning fixture found no threats.", ScanVerdict.Clean)]
 [InlineData(0, "Scan finished.\nThreat: Fixture", ScanVerdict.ThreatDetected)]
 [InlineData(2, "Scan finished.", ScanVerdict.ScanError)]
 [InlineData(0, "", ScanVerdict.ScanError)]
 [InlineData(0, "Scan finished. Error 0x80070005", ScanVerdict.ScanError)]
 public void DefenderIsFailClosed(int code, string text, ScanVerdict expected) => Assert.Equal(expected, DefenderScanner.Classify(new(code, text, "")));
 [Fact] public async Task SaveCollisionPreservesBothFilesAndHistoryHasNoUrl()
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-storage-test-" + Guid.NewGuid().ToString("N"));
  try
  {
   using var storage = new JobStorage(root);
   var job = storage.CreateJob(Guid.NewGuid()); var source = Path.Combine(job, "file.mp4"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
   var dest = Path.Combine(root, "saved"); Directory.CreateDirectory(dest); await File.WriteAllBytesAsync(Path.Combine(dest, "video.mp4"), [4]);
   var saved = await storage.CommitAsync(source, dest, "video", default);
   Assert.EndsWith("video (1).mp4", saved); Assert.Equal(new byte[] { 4 }, await File.ReadAllBytesAsync(Path.Combine(dest, "video.mp4")));
   await storage.RecordAsync(new("https://fixture.invalid/a?token=fixture", "fixture.invalid", "video.mp4", 3, 0, 0, 0, "fixture", "fixture", "成功", DateTimeOffset.Now, saved), default);
   var history = await File.ReadAllTextAsync(Path.Combine(root, "history.json")); Assert.DoesNotContain("token=fixture", history);
   await storage.CleanupAsync(job); Assert.False(Directory.Exists(job));
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
 }
 [Fact] public async Task CrossVolumeCopyVerifiesContentsAndRemovesStaging()
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-copy-test-" + Guid.NewGuid().ToString("N"));
  var destination = Path.Combine(root, "Saved");
  try
  {
   using var storage = new JobStorage(root, p => p.StartsWith(destination, StringComparison.OrdinalIgnoreCase) ? "destination-volume" : "source-volume");
   var job = storage.CreateJob(Guid.NewGuid()); var source = Path.Combine(job, "fixture.mp4"); var bytes = Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
   await File.WriteAllBytesAsync(source, bytes);
   var saved = await storage.CommitAsync(source, destination, "fixture", default);
   Assert.Equal(bytes, await File.ReadAllBytesAsync(saved)); Assert.True(File.Exists(source)); Assert.Empty(Directory.EnumerateFiles(destination, "*.partial"));
   await storage.CleanupAsync(job);
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
 }
 [Fact] public async Task RecoveryDoesNotDeleteAnActiveJob()
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-recovery-test-" + Guid.NewGuid().ToString("N"));
  try
  {
   using var storage = new JobStorage(root); var active = storage.CreateJob(Guid.NewGuid());
   var old = Path.Combine(root, "Jobs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(old); await File.WriteAllTextAsync(Path.Combine(old, ".lease"), "");
   await storage.RecoverAsync(); Assert.True(Directory.Exists(active)); Assert.False(Directory.Exists(old)); await storage.CleanupAsync(active);
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
 }
}
