using Mp4Downloader.Core;
using Xunit;
namespace Mp4Downloader.Core.Tests;

public class PlannerTests
{
 private static MediaInfo Media(string container, string video, string audio, bool fastStart) => new("fixture", container,
  new(video, "yuv420p", "High", 40, 1920, 1080, false, 10, "30/1"), audio == "none" ? null : new(audio, "LC", 2), 1000, fastStart);
 [Theory]
 [InlineData("mp4", "h264", "aac", true, Mp4Mode.PassThrough)]
 [InlineData("mp4", "h264", "aac", false, Mp4Mode.Remux)]
 [InlineData("matroska", "h264", "aac", false, Mp4Mode.Remux)]
 [InlineData("mpegts", "h264", "aac", false, Mp4Mode.Remux)]
 [InlineData("webm", "vp9", "opus", false, Mp4Mode.FullTranscode)]
 [InlineData("matroska", "h264", "opus", false, Mp4Mode.PartialTranscode)]
 [InlineData("matroska", "vp9", "aac", false, Mp4Mode.PartialTranscode)]
 [InlineData("mp4", "h264", "none", true, Mp4Mode.PassThrough)]
 public void ChoosesMinimalProcessing(string container, string video, string audio, bool fastStart, Mp4Mode expected)
  => Assert.Equal(expected, Mp4Planner.Create(Media(container, video, audio, fastStart)).Mode);
 [Theory]
 [InlineData("yuv420p10le")]
 [InlineData("yuv422p")]
 [InlineData("yuv444p")]
 public void NonCommonPixelFormatRequiresVideoEncoding(string format)
 {
  var media = Media("mp4", "h264", "aac", true);
  Assert.True(Mp4Planner.Create(media with { Video = media.Video! with { PixelFormat = format } }).EncodeVideo);
 }
 [Fact] public void AacHeRequiresAudioEncoding()
 {
  var media = Media("mp4", "h264", "aac", true);
  var plan = Mp4Planner.Create(media with { Audio = new("aac", "HE-AAC", 2) });
  Assert.False(plan.EncodeVideo); Assert.True(plan.EncodeAudio);
 }
 [Fact] public void HdrStopsBeforeTranscode()
 {
  var media = Media("webm", "vp9", "opus", false);
  Assert.Equal(FailureCode.Hdr, Assert.Throws<DownloadFailure>(() => Mp4Planner.Create(media with { Video = media.Video! with { Hdr = true } })).Code);
 }
 [Fact] public void ExtraStreamsRequireExplicitRemuxSelection()
 {
  var media = Media("mp4", "h264", "aac", true) with { ExtraStreams = true };
  Assert.Equal(Mp4Mode.Remux, Mp4Planner.Create(media).Mode);
  Assert.False(Mp4Planner.Compatible(media));
 }
 [Fact] public void AudioOnlyIsRejected() => Assert.Equal(FailureCode.AudioOnly, Assert.Throws<DownloadFailure>(() => Mp4Planner.Create(Media("mp4", "h264", "aac", true) with { Video = null })).Code);
 [Theory]
 [InlineData("file:///C:/video.mp4")]
 [InlineData("javascript:alert(1)")]
 [InlineData("https://user:password@example.invalid/")]
 [InlineData("not a URL")]
 public void RejectsUnsafeInput(string url) => Assert.Throws<DownloadFailure>(() => UrlPolicy.Parse(url));
 [Fact] public void OriginIncludesSchemeAndPort()
 {
  Assert.True(UrlPolicy.SameOrigin(new("https://example.invalid/a"), new("https://example.invalid/b")));
  Assert.False(UrlPolicy.SameOrigin(new("https://example.invalid/"), new("http://example.invalid/")));
  Assert.False(UrlPolicy.SameOrigin(new("https://example.invalid/"), new("https://example.invalid:444/")));
 }
}
