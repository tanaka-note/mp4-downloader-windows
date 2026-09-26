using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public static class SafeFileName
{
 public static string Base(string? title)
 {
  var name = Regex.Replace(PrivacyText.Redact(title ?? ""), "[\\x00-\\x1f<>:\"/\\\\|?*]", "_").Trim().TrimEnd('.', ' ');
  if (name.Length > 100) { name = name[..100]; if (char.IsHighSurrogate(name[^1])) name = name[..^1]; }
  if (name.Length == 0) name = "video";
  if (Regex.IsMatch(name.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase)) name = "_" + name;
  return name;
 }
}
public sealed class JobStorage : IJobStorage, IDisposable
{
 public string Root { get; }
 private readonly Dictionary<string, FileStream> leases = [];
 private readonly SemaphoreSlim historyGate = new(1, 1);
 private readonly Func<string, string> volumeIdentity;
 public string ActiveDirectory => leases.Keys.FirstOrDefault() ?? Path.Combine(Root, "Jobs");
 public JobStorage(string? root = null, Func<string, string>? volumeIdentity = null)
 {
  this.volumeIdentity = volumeIdentity ?? VolumeId;
  Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanakaNote", "Mp4Downloader"));
  LocalPathPolicy.Validate(Root);
  Directory.CreateDirectory(Path.Combine(Root, "Jobs"));
 }
 public string CreateJob(Guid id)
 {
  var directory = Path.Combine(Root, "Jobs", id.ToString("N"));
  Directory.CreateDirectory(directory);
  leases[directory] = new FileStream(Path.Combine(directory, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
  return directory;
 }
 public void EnsureSpace(string jobDirectory, string destination, long requiredBytes)
 {
  LocalPathPolicy.Validate(destination);
  Directory.CreateDirectory(destination);
  foreach (var location in new[] { jobDirectory, destination }.DistinctBy(Path.GetPathRoot))
  {
   var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(location))!);
   if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes) throw new DownloadFailure(FailureCode.DiskFull, "保存に必要な空き容量が不足しています。");
  }
 }
 public async Task<string> CommitAsync(string path, string destination, string title, CancellationToken ct)
 {
  destination = Path.GetFullPath(destination);
  LocalPathPolicy.Validate(destination);
  Directory.CreateDirectory(destination);
  var staged = Path.Combine(destination, $".mp4-downloader-{Guid.NewGuid():N}.partial");
  if (destination.StartsWith(@"\\", StringComparison.Ordinal)) throw new DownloadFailure(FailureCode.Save, "初期版の保存先はローカルドライブを指定してください。");
  var sameVolume = volumeIdentity(path).Equals(volumeIdentity(destination), StringComparison.OrdinalIgnoreCase);
  var jobRoot = FindJobRoot(path);
  var stageRecord = jobRoot is null ? null : Path.Combine(jobRoot, ".destination-stage.json");
  try
  {
   if (!sameVolume)
   {
    if (stageRecord is not null) await File.WriteAllTextAsync(stageRecord, JsonSerializer.Serialize(staged), ct);
    await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
    await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
    { await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
    await using var source = File.OpenRead(path);
    await using var target = File.OpenRead(staged);
    var sourceHash = await SHA256.HashDataAsync(source, ct);
    var targetHash = await SHA256.HashDataAsync(target, ct);
    if (source.Length != target.Length || !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
     throw new DownloadFailure(FailureCode.Save, "保存先へのコピー検証に失敗しました。");
   }
   var baseName = SafeFileName.Base(title);
   for (var i = 0; i < 10000; i++)
   {
    ct.ThrowIfCancellationRequested();
    var target = Path.Combine(destination, baseName + (i == 0 ? "" : $" ({i})") + ".mp4");
    try { File.Move(sameVolume ? path : staged, target, overwrite: false); return target; }
    catch (IOException) when (File.Exists(target)) { }
   }
   throw new DownloadFailure(FailureCode.Save, "一意な保存名を作成できませんでした。");
  }
  finally
  {
   var removed = TryDelete(staged);
   if (removed && stageRecord is not null) TryDelete(stageRecord);
  }
 }
 public async Task RecordAsync(HistoryEntry entry, CancellationToken ct)
 {
  await historyGate.WaitAsync(ct);
  try
  {
   var path = Path.Combine(Root, "history.json");
   var history = await ReadHistoryAsync(ct);
   history.Insert(0, entry with { Title = SanitizeTitle(entry.Title) });
   var temp = Path.Combine(Root, "history.next.json");
   await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(history.Take(500)), ct);
   File.Move(temp, path, true);
  }
  finally { historyGate.Release(); }
 }
 public async Task<List<HistoryEntry>> ReadHistoryAsync(CancellationToken ct = default)
 {
  var path = Path.Combine(Root, "history.json");
  if (!File.Exists(path)) return [];
  try { return JsonSerializer.Deserialize<List<HistoryEntry>>(await File.ReadAllTextAsync(path, ct)) ?? []; }
  catch (JsonException) { return []; }
 }
 private static string SanitizeTitle(string title) => PrivacyText.Redact(title);
 public void ClearHistory() { var path = Path.Combine(Root, "history.json"); if (File.Exists(path)) File.Delete(path); }
 public async Task CleanupAsync(string directory)
 {
  var full = Path.GetFullPath(directory);
  var owned = Path.Combine(Root, "Jobs") + Path.DirectorySeparatorChar;
  if (!full.StartsWith(owned, StringComparison.OrdinalIgnoreCase)) return;
  if (leases.Remove(full, out var lease)) lease.Dispose();
  for (var attempt = 0; attempt < 3; attempt++)
  {
   try
   {
    RecoverStage(full);
    if (Directory.Exists(full)) DeleteOwnedTree(full);
    return;
   }
   catch (IOException) { await Task.Delay(100 * (attempt + 1)); }
   catch (UnauthorizedAccessException) { return; }
  }
 }
 public async Task RecoverAsync()
 {
  foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "Jobs")))
  {
   if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || leases.ContainsKey(directory)) continue;
   if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
   try { using var lease = new FileStream(Path.Combine(directory, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
   catch (IOException) { continue; }
   await CleanupAsync(directory);
  }
 }
 public void Dispose() { foreach (var lease in leases.Values) lease.Dispose(); leases.Clear(); }
 private string? FindJobRoot(string path)
 {
  var jobs = Path.Combine(Root, "Jobs") + Path.DirectorySeparatorChar;
  var full = Path.GetFullPath(path);
  if (!full.StartsWith(jobs, StringComparison.OrdinalIgnoreCase)) return null;
  var id = full[jobs.Length..].Split(Path.DirectorySeparatorChar)[0];
  return Guid.TryParseExact(id, "N", out _) ? Path.Combine(jobs, id) : null;
 }
 private static void RecoverStage(string directory)
 {
  var record = Path.Combine(directory, ".destination-stage.json");
  if (!File.Exists(record)) return;
  string? stage;
  try { stage = JsonSerializer.Deserialize<string>(File.ReadAllText(record)); }
  catch (JsonException) { return; }
  if (stage is not null && Path.IsPathFullyQualified(stage) && Regex.IsMatch(Path.GetFileName(stage), @"^\.mp4-downloader-[a-f0-9]{32}\.partial$") && File.Exists(stage))
  { try { LocalPathPolicy.Validate(stage); } catch (DownloadFailure) { return; } File.Delete(stage); }
 }
 private static bool TryDelete(string path)
 {
  try { if (File.Exists(path)) File.Delete(path); return true; }
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
 }
 private static void DeleteOwnedTree(string directory)
 {
  if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(directory); return; }
  foreach (var child in Directory.EnumerateDirectories(directory)) DeleteOwnedTree(child);
  foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
  Directory.Delete(directory);
 }
 public static string VolumeId(string path)
 {
  if (!OperatingSystem.IsWindows()) return Path.GetPathRoot(Path.GetFullPath(path))!;
  var mount = new StringBuilder(512); var name = new StringBuilder(512);
  if (!GetVolumePathNameW(Path.GetFullPath(path), mount, mount.Capacity) || !GetVolumeNameForVolumeMountPointW(mount.ToString(), name, name.Capacity))
   throw new DownloadFailure(FailureCode.Save, "保存先のボリュームを確認できませんでした。");
  return name.ToString();
 }
 [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetVolumePathNameW(string path, StringBuilder mount, int length);
 [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, int length);
}

public static class LocalPathPolicy
{
 public static void Validate(string path)
 {
  var full = Path.GetFullPath(path);
  if (full.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full))
   throw new DownloadFailure(FailureCode.Save, "保存先はローカルドライブのフォルダを指定してください。");
  for (var current = full; current is not null; current = Path.GetDirectoryName(current))
   if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
    throw new DownloadFailure(FailureCode.Save, "リンクまたはjunctionを含む保存先・作業領域には対応していません。");
 }
}

public static class PrivacyText
{
 public static string Redact(string text) => Regex.Replace(text.Length > 300 ? text[..300] : text, @"https?://\S+|(?i)(token|authorization|cookie|password|credential|signature|session)\s*[:=]\s*\S+", "[非保存]");
}
