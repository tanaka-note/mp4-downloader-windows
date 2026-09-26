using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed class ToolCatalog(string directory)
{
 public string Directory { get; } = Path.GetFullPath(directory);
 public string Find(string name)
 {
  var path = Path.Combine(Directory, name + ".exe");
  var manifest = Path.Combine(Directory, "binary-hashes.json");
  if (!File.Exists(path) || !File.Exists(manifest)) throw new DownloadFailure(FailureCode.Tool, "必要な動画ツールがありません。検証済みpublishフォルダを使用してください。");
  var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest))!;
  using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
  var actual = Convert.ToHexString(SHA256.HashData(file));
  if (!hashes.TryGetValue(name + ".exe", out var expected) || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
   throw new DownloadFailure(FailureCode.Tool, "動画ツールの整合性を確認できませんでした。");
  return path;
 }
}
public sealed class ProcessRunner
{
 public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
  TimeSpan timeout, CancellationToken ct, int outputLimit = 4 * 1024 * 1024)
 {
  var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
   RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory,
   StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
  foreach (var argument in arguments) start.ArgumentList.Add(argument);
  start.Environment["DENO_NO_UPDATE_CHECK"] = "1";
  start.Environment["DENO_NO_PROMPT"] = "1";
  using var process = new Process { StartInfo = start };
  using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
  deadline.CancelAfter(timeout);
  using var job = new KillOnCloseJob();
  var started = false;
  try
  {
   if (!process.Start()) throw new DownloadFailure(FailureCode.Tool, "動画ツールを起動できませんでした。");
   started = true;
   job.Assign(process);
   var output = CaptureAsync(process.StandardOutput, outputLimit, deadline.Token);
   var error = CaptureAsync(process.StandardError, 128 * 1024, deadline.Token);
   await process.WaitForExitAsync(deadline.Token);
   return new(process.ExitCode, await output, await error);
  }
  catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DownloadFailure(FailureCode.Tool, "動画ツールの処理時間が上限を超えました。"); }
  catch (System.ComponentModel.Win32Exception) { throw new DownloadFailure(FailureCode.Tool, "動画ツールを起動できませんでした。"); }
  finally
  {
   if (started && !process.HasExited)
   {
    // CLI adapters have no shared safe graceful-cancel protocol. A brief wait precedes tree termination.
    try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(250)).Token); }
    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
    await process.WaitForExitAsync(CancellationToken.None);
   }
  }
 }
 private static async Task<string> CaptureAsync(StreamReader reader, int limit, CancellationToken ct)
 {
  var output = new StringBuilder();
  var buffer = new char[4096];
  var overflow = false;
  while (true)
  {
   var count = await reader.ReadAsync(buffer.AsMemory(), ct);
   if (count == 0) break;
   if (output.Length + count > limit) overflow = true;
   if (!overflow) output.Append(buffer, 0, count);
  }
  if (overflow) throw new DownloadFailure(FailureCode.Tool, "動画ツールの応答が上限を超えました。");
  return output.ToString();
 }
}

internal sealed class KillOnCloseJob : IDisposable
{
 private nint handle;
 public KillOnCloseJob()
 {
  if (!OperatingSystem.IsWindows()) return;
  handle = CreateJobObjectW(0, null);
  if (handle == 0) throw new DownloadFailure(FailureCode.Tool, "安全なプロセス管理を初期化できませんでした。");
  var info = new ExtendedLimits();
  info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
  if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
  { Dispose(); throw new DownloadFailure(FailureCode.Tool, "安全なプロセス管理を初期化できませんでした。"); }
 }
 public void Assign(Process process)
 {
  if (handle != 0 && !AssignProcessToJobObject(handle, process.Handle))
  { process.Kill(true); throw new DownloadFailure(FailureCode.Tool, "動画ツールを安全に管理できませんでした。"); }
 }
 public void Dispose() { if (handle != 0) { CloseHandle(handle); handle = 0; } }
 [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorking, MaxWorking; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
 [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
 [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
 [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
 [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);
 [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
 [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
