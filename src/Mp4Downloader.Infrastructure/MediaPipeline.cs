using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class MediaProbe(ToolCatalog tools, ProcessRunner processes)
{
 public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
 {
  if (!File.Exists(path) || new FileInfo(path).Length < 32) throw new DownloadFailure(FailureCode.Corrupt, "動画ファイルが空または破損しています。");
  var result = await processes.RunAsync(tools.Find("ffprobe"), ["-v", "error", "-protocol_whitelist", "file", "-show_format", "-show_streams", "-of", "json", path], Path.GetDirectoryName(path)!, TimeSpan.FromSeconds(60), ct);
  if (result.ExitCode != 0) throw new DownloadFailure(FailureCode.Corrupt, "動画ファイルを解析できませんでした。");
  try
  {
   using var document = JsonDocument.Parse(result.Output);
   var root = document.RootElement;
   var format = root.GetProperty("format");
   var container = Text(format, "format_name");
   var duration = Number(format, "duration");
   var streamCount = root.GetProperty("streams").GetArrayLength();
   VideoInfo? video = null;
   AudioInfo? audio = null;
   foreach (var stream in root.GetProperty("streams").EnumerateArray())
   {
    if (Text(stream, "codec_tag_string") is "encv" or "enca") throw new DownloadFailure(FailureCode.Drm, "この動画はDRMで保護されています。");
    var index = Integer(stream, "index");
    if (Text(stream, "codec_type") == "video" && video is null && !(stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") == 1))
    {
     var hdr = Text(stream, "color_transfer") is "smpte2084" or "arib-std-b67" || Text(stream, "color_primaries") == "bt2020" || Text(stream, "color_space") is "bt2020nc" or "bt2020c";
     var rotation = 0;
     if (stream.TryGetProperty("side_data_list", out var sideData)) foreach (var side in sideData.EnumerateArray())
     { hdr |= Text(side, "side_data_type").Contains("Mastering", StringComparison.Ordinal) || Text(side, "side_data_type").Contains("DOVI", StringComparison.Ordinal); if (side.TryGetProperty("rotation", out var value)) rotation = value.GetInt32(); }
     video = new(Text(stream, "codec_name"), Text(stream, "pix_fmt"), Text(stream, "profile"), Integer(stream, "level"), Integer(stream, "width"), Integer(stream, "height"), hdr,
      Number(stream, "duration") is > 0 and var streamDuration ? streamDuration : duration, Text(stream, "avg_frame_rate"), rotation, index);
    }
    if (Text(stream, "codec_type") == "audio" && audio is null) audio = new(Text(stream, "codec_name"), Text(stream, "profile"), Integer(stream, "channels"), index);
   }
   return new(path, container, video, audio, new FileInfo(path).Length, container.Split(',').Contains("mp4") && Mp4Atoms.HasFastStart(path), streamCount > (video is null ? 0 : 1) + (audio is null ? 0 : 1));
  }
  catch (JsonException) { throw new DownloadFailure(FailureCode.Corrupt, "動画の解析結果が不正です。"); }
 }
 private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
 private static int Integer(JsonElement element, string name) => int.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var result) ? result : 0;
 private static double Number(JsonElement element, string name) => double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var result) ? result : 0;
}
public static class Mp4Atoms
{
 public static bool HasFastStart(string path)
 {
  using var file = File.OpenRead(path);
  Span<byte> header = stackalloc byte[16];
  var mediaSeen = false;
  var moovSeen = false;
  while (file.Position + 8 <= file.Length)
  {
   var start = file.Position;
   file.ReadExactly(header[..8]);
   long length = BinaryPrimitives.ReadUInt32BigEndian(header);
   var type = Encoding.ASCII.GetString(header.Slice(4, 4));
   var headerLength = 8;
   if (length == 1) { file.ReadExactly(header.Slice(8, 8)); length = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header[8..])); headerLength = 16; }
   if (length == 0) length = file.Length - start;
   if (length < headerLength || length > file.Length - start) throw new DownloadFailure(FailureCode.Corrupt, "MP4の構造が破損しています。");
   if (type == "pssh") throw new DownloadFailure(FailureCode.Drm, "この動画はDRMで保護されています。");
   if (type == "moov")
   {
    if (length > 32 * 1024 * 1024) throw new DownloadFailure(FailureCode.Unsupported, "MP4の解析領域が上限を超えています。");
    var data = new byte[(int)length - headerLength];
    file.ReadExactly(data);
    // Conservative detection within the metadata atom; no license/key operations are performed.
    var metadata = Encoding.Latin1.GetString(data);
    if (metadata.Contains("pssh", StringComparison.Ordinal) || metadata.Contains("sinf", StringComparison.Ordinal) || metadata.Contains("tenc", StringComparison.Ordinal))
     throw new DownloadFailure(FailureCode.Drm, "この動画はDRMで保護されています。");
    moovSeen = !mediaSeen;
   }
   if (type == "mdat") mediaSeen = true;
   file.Position = start + length;
  }
  return moovSeen;
 }
}
public sealed class MediaPipeline(ToolCatalog tools, ProcessRunner processes, MediaProbe probe) : IMediaPipeline
{
 public async Task<(string Path, MediaInfo Media, Mp4Plan Plan)> NormalizeAsync(AcquisitionResult input, string directory, IProgress<JobProgress> progress, CancellationToken ct)
 {
  if (!input.Complete) throw new DownloadFailure(FailureCode.Corrupt, "動画の取得が完了していません。");
  var infos = new List<MediaInfo>();
  foreach (var file in input.Files) infos.Add(await probe.ProbeAsync(file, ct));
  var video = infos.FirstOrDefault(m => m.Video is not null) ?? throw new DownloadFailure(FailureCode.AudioOnly, "音声のみのコンテンツには対応していません。");
  var audio = infos.FirstOrDefault(m => m.Video is null && m.Audio is not null) ?? infos.FirstOrDefault(m => m.Audio is not null);
  var combined = video with { Audio = audio?.Audio };
  var plan = Mp4Planner.Create(combined, infos.Count > 1);
  var path = video.Path;
  if (plan.Mode != Mp4Mode.PassThrough)
  {
   progress.Report(new(JobStage.Normalizing, plan.EncodeVideo || plan.EncodeAudio ? "MP4へ変換しています" : "再エンコードせずMP4へ整理しています"));
   var required = infos.Sum(i => i.Size) * 2 + 64L * 1024 * 1024;
   if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < required) throw new DownloadFailure(FailureCode.DiskFull, "MP4処理の空き容量が不足しています。");
   path = Path.Combine(directory, "normalized.mp4");
   var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file", "-i", video.Path };
   var separateAudio = audio is not null && audio.Path != video.Path;
   if (separateAudio) args.AddRange(["-protocol_whitelist", "file", "-i", audio!.Path]);
   args.AddRange(["-map", $"0:{video.Video!.StreamIndex}"]);
   if (audio is not null) args.AddRange(["-map", $"{(separateAudio ? 1 : 0)}:{audio.Audio!.StreamIndex}"]);
   args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn", "-c:v", plan.EncodeVideo ? "libx264" : "copy"]);
   if (plan.EncodeVideo) args.AddRange(["-crf", "19", "-preset", "medium", "-pix_fmt", "yuv420p", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-fps_mode", "passthrough"]);
   if (audio is not null)
   {
    args.AddRange(["-c:a", plan.EncodeAudio ? "aac" : "copy"]);
    if (plan.EncodeAudio) args.AddRange(["-profile:a", "aac_low", "-b:a", "192k", "-ac", Math.Min(6, audio.Audio!.Channels).ToString(CultureInfo.InvariantCulture)]);
   }
   args.AddRange(["-movflags", "+faststart", "-f", "mp4", path]);
   var result = await processes.RunAsync(tools.Find("ffmpeg"), args, directory, TimeSpan.FromHours(12), ct);
   if (result.ExitCode != 0) throw new DownloadFailure(FailureCode.Corrupt, "MP4処理を完了できませんでした。");
  }
  progress.Report(new(JobStage.Validating, "完成MP4を検証しています"));
  var final = await probe.ProbeAsync(path, ct);
  if (!Mp4Planner.Compatible(final) || final.Video!.Duration <= 0 || !double.IsFinite(final.Video.Duration) || audio is not null && final.Audio is null)
   throw new DownloadFailure(FailureCode.Corrupt, "完成MP4が互換性またはトラック条件を満たしません。");
  var expected = input.ExpectedDuration ?? video.Video!.Duration;
  if (expected > 0 && Math.Abs(final.Video.Duration - expected) > Math.Max(2, expected * 0.02))
   throw new DownloadFailure(FailureCode.Corrupt, "完成MP4の再生時間が一致しません。");
  if (final.Video.Duration < 2 || expected <= 0)
  {
   var decoded = await processes.RunAsync(tools.Find("ffmpeg"), ["-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file", "-i", path,
    "-map", "0:v:0", "-t", "10", "-f", "null", "-"], directory, TimeSpan.FromMinutes(2), ct);
   if (decoded.ExitCode != 0) throw new DownloadFailure(FailureCode.Corrupt, "動画の追加検証に失敗しました。");
  }
  return (path, final, plan);
 }
}
