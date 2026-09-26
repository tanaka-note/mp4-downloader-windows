namespace Mp4Downloader.Core;

public enum MediaKind { Direct, Hls, Dash, Site }
public enum Protection { Clear, Unknown, Encrypted, Drm }
public enum JobStage { Resolving, Selecting, Downloading, Probing, Normalizing, Validating, Scanning, Saving, Completed, Failed, Cancelled }
public enum Mp4Mode { PassThrough, Remux, PartialTranscode, FullTranscode }
public enum ScanVerdict { Clean, ThreatDetected, ScanUnavailable, ScanError }
public enum FailureCode { NoMatch, InvalidUrl, AccessRequired, Unsupported, Drm, Encrypted, Hdr, AudioOnly, Network, RateLimited, Corrupt, DiskFull, Scan, Tool, Save }
public sealed class DownloadFailure(FailureCode code, string safeMessage) : Exception(safeMessage)
{
 public FailureCode Code { get; } = code;
 public bool CanFallback => Code is FailureCode.NoMatch or FailureCode.Unsupported or FailureCode.Network or FailureCode.Corrupt;
}
public sealed record Track(string Id, Uri Url, string Role, string? Codec = null, string? Language = null, string? Manifest = null);
// This model is intentionally transient. URLs must never be persisted or logged.
public sealed record PlaybackCandidate(string Id, string Title, Uri ParentPage, MediaKind Kind, IReadOnlyList<Track> Tracks,
 Protection Protection, string Evidence, bool Viable, double? Duration = null, int? Width = null, int? Height = null,
 string? AuthContextId = null, string? Snapshot = null, bool Refreshable = false);
public sealed record AcquisitionPlan(PlaybackCandidate Candidate, string Engine);
public sealed record AcquisitionResult(IReadOnlyList<string> Files, string Engine, bool Complete, double? ExpectedDuration = null);
public sealed record VideoInfo(string Codec, string PixelFormat, string Profile, int Level, int Width, int Height,
 bool Hdr, double Duration, string FrameRate, int Rotation = 0, int StreamIndex = 0);
public sealed record AudioInfo(string Codec, string Profile, int Channels, int StreamIndex = 0);
public sealed record MediaInfo(string Path, string Container, VideoInfo? Video, AudioInfo? Audio, long Size, bool FastStart, bool ExtraStreams = false);
public sealed record Mp4Plan(Mp4Mode Mode, bool EncodeVideo, bool EncodeAudio);
public sealed record JobProgress(JobStage Stage, string Message, double? Fraction = null);
public sealed record HistoryEntry(string Title, string Host, string FileName, long Size, int Width, int Height,
 double Duration, string Engine, string Mp4Mode, string Status, DateTimeOffset CompletedAt, string? SavedPath);
public sealed record JobOutcome(JobStage Stage, string Message, string? SavedPath = null);

public interface IResolver
{
 string Name { get; }
 Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct);
}
public interface ICandidateSelector
{
 Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates, CancellationToken ct);
}
public interface IAcquisitionPlanner { IReadOnlyList<AcquisitionPlan> Plan(PlaybackCandidate candidate); }
public interface IDownloadEngine
{
 string Name { get; }
 Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct);
}
public interface IMediaPipeline
{
 Task<(string Path, MediaInfo Media, Mp4Plan Plan)> NormalizeAsync(AcquisitionResult input, string directory, IProgress<JobProgress> progress, CancellationToken ct);
}
public interface IScanner { Task<ScanVerdict> ScanAsync(string path, CancellationToken ct); }
public interface IJobStorage
{
 string CreateJob(Guid id);
 void EnsureSpace(string jobDirectory, string destination, long requiredBytes);
 Task<string> CommitAsync(string path, string destination, string title, CancellationToken ct);
 Task RecordAsync(HistoryEntry entry, CancellationToken ct);
 Task CleanupAsync(string directory);
}

public static class UrlPolicy
{
 public static Uri Parse(string text)
 {
  if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
      !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
   throw new DownloadFailure(FailureCode.InvalidUrl, "HTTPまたはHTTPSの動画URLを入力してください。");
  return uri;
 }
 public static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
}

public static class Mp4Planner
{
 public static Mp4Plan Create(MediaInfo media, bool separateTracks = false)
 {
  var video = media.Video ?? throw new DownloadFailure(FailureCode.AudioOnly, "音声のみのコンテンツには対応していません。");
  if (video.Hdr) throw new DownloadFailure(FailureCode.Hdr, "HDR動画の変換は現在サポートしていません。");
  if (video.Width > 4096 || video.Height > 2304)
   throw new DownloadFailure(FailureCode.Unsupported, "この解像度は現在のMP4互換性範囲を超えています。");
  var v = video.Codec == "h264" && video.PixelFormat == "yuv420p" && video.Profile is "Constrained Baseline" or "Baseline" or "Main" or "High" && video.Level is > 0 and <= 52;
  var a = media.Audio is null || media.Audio is { Codec: "aac", Profile: "LC", Channels: <= 6 };
  var mp4 = media.Container.Split(',').Contains("mp4") && media.FastStart && !media.ExtraStreams;
  var mode = v && a ? (mp4 && !separateTracks && !media.ExtraStreams ? Mp4Mode.PassThrough : Mp4Mode.Remux) : (!v && !a ? Mp4Mode.FullTranscode : Mp4Mode.PartialTranscode);
  return new(mode, !v, !a);
 }
 public static bool Compatible(MediaInfo media)
 {
  try { var plan = Create(media); return !plan.EncodeVideo && !plan.EncodeAudio && media.Container.Split(',').Contains("mp4") && media.FastStart && !media.ExtraStreams; }
  catch (DownloadFailure) { return false; }
 }
}
