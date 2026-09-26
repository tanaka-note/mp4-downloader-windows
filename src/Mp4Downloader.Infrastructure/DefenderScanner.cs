using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed class DefenderScanner(ProcessRunner processes, Func<string?>? executableLocator = null,
 Func<string, string, CancellationToken, Task<ProcessResult>>? scanProcess = null) : IScanner
{
 public async Task<ScanVerdict> ScanAsync(string path, CancellationToken ct)
 {
  if (!OperatingSystem.IsWindows()) return ScanVerdict.ScanUnavailable;
  try
  {
   var executable = (executableLocator ?? FindExecutable)();
   if (executable is null) return ScanVerdict.ScanUnavailable;
   var before = await HashAsync(path, ct);
   var result = scanProcess is null ? await processes.RunAsync(executable, ["-Scan", "-ScanType", "3", "-DisableRemediation", "-File", path], Path.GetDirectoryName(path)!, TimeSpan.FromMinutes(10), ct) : await scanProcess(executable, path, ct);
   var verdict = Classify(result);
   if (verdict != ScanVerdict.Clean) return verdict;
   if (!File.Exists(path)) return ScanVerdict.ScanError;
   var after = await HashAsync(path, ct);
   if (!CryptographicOperations.FixedTimeEquals(before, after)) return ScanVerdict.ScanError;
   return ScanVerdict.Clean;
  }
  catch (DownloadFailure) { return ScanVerdict.ScanError; }
  catch (IOException) { return ScanVerdict.ScanError; }
  catch (UnauthorizedAccessException) { return ScanVerdict.ScanUnavailable; }
 }
 public static ScanVerdict Classify(ProcessResult result)
 {
  var text = result.Output + "\n" + result.Error;
  // DisableRemediation is mandatory. Unknown/localized output is fail-closed, never guessed clean.
  if (Regex.IsMatch(text, @"(?im)^\s*(Threat\s*:|Threat Name\s*:|Threats?\s+(?:found|detected)\s*[:=]\s*[1-9])")) return ScanVerdict.ThreatDetected;
  if (result.ExitCode != 0 || Regex.IsMatch(text, @"(?i)\b(error|failed|0x[8c][0-9a-f]{7})\b")) return ScanVerdict.ScanError;
  if (!text.Contains("Scan finished", StringComparison.OrdinalIgnoreCase) || !text.Contains("found no threats", StringComparison.OrdinalIgnoreCase)) return ScanVerdict.ScanError;
  return ScanVerdict.Clean;
 }
 private static async Task<byte[]> HashAsync(string path, CancellationToken ct) { await using var input = File.OpenRead(path); return await SHA256.HashDataAsync(input, ct); }
 public static string? FindExecutable()
 {
  var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
  if (Directory.Exists(platform))
  {
   var result = Directory.EnumerateDirectories(platform).OrderByDescending(p => Version.TryParse(Path.GetFileName(p).Split('-')[0], out var version) ? version : new Version())
    .Select(p => Path.Combine(p, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
   if (result is not null) return result;
  }
  var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
  return File.Exists(fallback) ? fallback : null;
 }
}
