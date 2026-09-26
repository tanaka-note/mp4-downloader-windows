using System.Text.Json;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using Xunit;

namespace Mp4Downloader.IntegrationTests;

public class ReleaseSafetyTests
{
 [Theory]
 [InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("192.168.0.1")][InlineData("169.254.169.254")]
 [InlineData("172.16.0.1")][InlineData("100.64.0.1")][InlineData("::1")][InlineData("fd00::1")][InlineData("fe80::1")][InlineData("::ffff:127.0.0.1")]
 public void NonPublicAddressesAreRejected(string address)=>Assert.False(NetworkPolicy.IsPublic(System.Net.IPAddress.Parse(address)));
 [Fact] public async Task RealSocketPolicyBlocksLoopback() => await Assert.ThrowsAsync<DownloadFailure>(()=>NetworkPolicy.PublicAddressesAsync("127.0.0.1",default));
 [Theory]
 [InlineData("http://127.0.0.1/")][InlineData("http://localhost/")][InlineData("https://127.0.0.1/")]
 public async Task ExternalToolProxyBlocksPrivateTargets(string url)
 {
  using var proxy=new PublicNetworkProxy();
  using var client=new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler {Proxy=new System.Net.WebProxy(proxy.Address),UseProxy=true}) {Timeout=TimeSpan.FromSeconds(5)};
  if(url.StartsWith("https")) await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(()=>client.GetAsync(url));
  else {using var response=await client.GetAsync(url);Assert.Equal(System.Net.HttpStatusCode.Forbidden,response.StatusCode);}
 }
 [Fact] public void SecretLookingTitlesCannotBecomePersistentFilenames()
 {
  var safe=SafeFileName.Base("video token=private-fixture https://example.invalid/file?sig=private-fixture");
  Assert.DoesNotContain("private-fixture",safe);
 }
 [Fact] public async Task JunctionDestinationIsRejectedAndCleanupDoesNotFollowChildJunction()
 {
  var root=Path.Combine(Path.GetTempPath(),"mp4-junction-"+Guid.NewGuid().ToString("N"));
  var outside=Path.Combine(Path.GetTempPath(),"mp4-outside-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(outside);await File.WriteAllTextAsync(Path.Combine(outside,"keep.txt"),"keep");
  try
  {
   using var storage=new JobStorage(root);var job=storage.CreateJob(Guid.NewGuid());var link=Path.Combine(job,"link");
   var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   start.Environment["MP4_TEST_LINK"]=link; start.Environment["MP4_TEST_TARGET"]=outside;
   foreach(var argument in new[]{"-NoProfile","-Command","New-Item -ItemType Junction -Path $env:MP4_TEST_LINK -Target $env:MP4_TEST_TARGET -ErrorAction Stop | Out-Null"})start.ArgumentList.Add(argument);
   using var process=System.Diagnostics.Process.Start(start)!;await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);
   Assert.Throws<DownloadFailure>(()=>storage.EnsureSpace(job,link,1));
   await storage.CleanupAsync(job);Assert.False(Directory.Exists(job));Assert.True(File.Exists(Path.Combine(outside,"keep.txt")));
  }
  finally {if(Directory.Exists(root))Directory.Delete(root,true);if(Directory.Exists(outside))Directory.Delete(outside,true);}
 }
 [Theory]
 [InlineData("../..\\evil:*.mp4")]
 [InlineData("CON")][InlineData("PRN")][InlineData("AUX")][InlineData("NUL")]
 [InlineData("COM1")][InlineData("COM9")][InlineData("LPT9")]
 [InlineData("name. ")][InlineData("<>:\"/\\|?*")]
 public async Task UntrustedTitlesStayInsideDestination(string title)
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-filename-" + Guid.NewGuid().ToString("N"));
  try
  {
   using var storage = new JobStorage(root); var job = storage.CreateJob(Guid.NewGuid());
   var input = Path.Combine(job, "input"); await File.WriteAllBytesAsync(input, [1,2,3]);
   var destination = Path.Combine(root,"Saved");
   var saved = await storage.CommitAsync(input, destination, title, default);
   Assert.Equal(destination, Path.GetDirectoryName(saved)); Assert.Equal(".mp4", Path.GetExtension(saved));
   await storage.CleanupAsync(job);
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
 }
 [Fact] public void LongUnicodeTitleIsBoundedAndHasNoTrailingSurrogate()
 {
  var name = SafeFileName.Base(new string('あ', 99) + "😀" + new string('a', 300));
  Assert.True(name.Length <= 100); Assert.False(char.IsHighSurrogate(name[^1]));
 }
 [Fact] public void UncAndDevicePathsAreRejectedBeforeWriting() => Assert.Throws<DownloadFailure>(() => LocalPathPolicy.Validate(@"\\server\share\video"));
 [Fact] public async Task CrossVolumeCancellationKeepsSourceAndLeavesNoFinalFile()
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-cancel-save-" + Guid.NewGuid().ToString("N"));
  var destination = Path.Combine(root,"Saved");
  try
  {
   using var storage = new JobStorage(root, p => p.StartsWith(destination) ? "target" : "source");
   var job = storage.CreateJob(Guid.NewGuid()); var input = Path.Combine(job,"input"); await File.WriteAllBytesAsync(input,new byte[10000]);
   using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
   await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.CommitAsync(input,destination,"video",cancelled.Token));
   Assert.True(File.Exists(input)); Assert.Empty(Directory.EnumerateFiles(destination)); await storage.CleanupAsync(job);
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
 }
 [Fact] public async Task RecoveryRemovesOnlyRecordedStage()
 {
  var root = Path.Combine(Path.GetTempPath(), "mp4-stage-recovery-" + Guid.NewGuid().ToString("N"));
  try
  {
   using var storage = new JobStorage(root); var job = storage.CreateJob(Guid.NewGuid());
   var stage = Path.Combine(root,$".mp4-downloader-{Guid.NewGuid():N}.partial"); var unrelated=Path.Combine(root,"unrelated.partial");
   await File.WriteAllTextAsync(stage,"partial"); await File.WriteAllTextAsync(unrelated,"keep");
   await File.WriteAllTextAsync(Path.Combine(job,".destination-stage.json"),JsonSerializer.Serialize(stage));
   await storage.CleanupAsync(job); Assert.False(File.Exists(stage)); Assert.True(File.Exists(unrelated));
  }
  finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
 }
 [Theory]
 [InlineData("disabled")][InlineData("missing")][InlineData("service")][InlineData("timeout")][InlineData("failure")][InlineData("malware")][InlineData("modified")]
 public async Task DefenderAbnormalStatesNeverBecomeClean(string mode)
 {
  var file=Path.GetTempFileName(); await File.WriteAllTextAsync(file,"synthetic clean fixture");
  try
  {
   var scanner = new DefenderScanner(new ProcessRunner(), () => mode == "missing" ? null : "fixture.exe", async (_,path,ct) =>
   {
    if (mode=="timeout") throw new DownloadFailure(FailureCode.Tool,"timeout");
    if (mode=="modified") await File.AppendAllTextAsync(path,"changed",ct);
    return mode switch
    {
     "malware" => new(0,"Scan finished.\nThreat: synthetic", ""),
     "disabled" or "service" => new(2,"Failed 0x800106ba", ""),
     "failure" => new(1,"Scan failed", ""),
     _ => new ProcessResult(0,"Scan finished. Scanning fixture found no threats.", "")
    };
   });
   Assert.NotEqual(ScanVerdict.Clean, await scanner.ScanAsync(file,default));
  }
  finally { File.Delete(file); }
 }
}
