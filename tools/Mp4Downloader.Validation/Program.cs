using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;

var repo = Path.GetFullPath(args.Length > 1 ? args[1] : ".");
var mode = args.FirstOrDefault() ?? "--public";
var root = Path.Combine(repo,"artifacts","validation-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var tools = new ToolCatalog(args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(repo,"tools","bin")); var runner=new ProcessRunner();
var vault=new AuthVault(); using var http = new HttpTransport(vault,allowPrivateNetwork: mode=="--http"); var manifest=new ManifestResolver(http);
using var storage = new JobStorage(Path.Combine(root,"Data"));
var coordinator = new DownloadCoordinator(mode is "--site" or "--yt-direct" ? [new YtDlpResolver(tools,runner,manifest,vault,()=>storage.ActiveDirectory),new HtmlResolver(http,manifest)] : [new DirectResolver(http),manifest],new Selection(),new AcquisitionPlanner(),
 [new DirectDownloadEngine(http),new ManifestDownloadEngine(tools,runner),new HlsDownloadEngine(http)],new MediaPipeline(tools,runner,new MediaProbe(tools,runner)),new DefenderScanner(runner),storage);
var results = new List<object>();
async Task Download(string name,string url)
{
 using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(5));
 var outcome=await coordinator.RunAsync(url,Path.Combine(root,"Saved"),new Progress<JobProgress>(),limit.Token);
 var history=await storage.ReadHistoryAsync();
 results.Add(new { Name=name, Host=new Uri(url).Host, Outcome=outcome.Stage.ToString(), Message=outcome.Message,
  Size=outcome.SavedPath is null ? 0 : new FileInfo(outcome.SavedPath).Length, Engine=history.FirstOrDefault()?.Engine,
  Mode=history.FirstOrDefault()?.Mp4Mode, Cleanup=!Directory.EnumerateDirectories(Path.Combine(storage.Root,"Jobs")).Any() });
 await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
 Console.WriteLine($"{name}: {outcome.Stage}; {outcome.Message}");
 if(outcome.Stage!=JobStage.Completed) throw new InvalidOperationException("Validation failed: "+name);
}
if(mode=="--public")
{
 await Download("Public MP4 + query","https://www.w3schools.com/html/mov_bbb.mp4?mp4-downloader-test=1");
 await Download("Public clear HLS","https://storage.googleapis.com/shaka-demo-assets/angel-one-hls/hls.m3u8");
 await Download("Public clear DASH","https://storage.googleapis.com/shaka-demo-assets/angel-one/dash.mpd");
}
else if(mode=="--site") await Download("Public video page, HTML fallback after extractor playlist rejection","https://www.w3schools.com/html/html5_video.asp");
else if(mode=="--yt-direct") await Download("yt-dlp generic extraction + direct acquisition","https://www.w3schools.com/html/mov_bbb.mp4");
else if(mode=="--site-diagnose")
{
 using var proxy=new PublicNetworkProxy();
 var result=await runner.RunAsync(tools.Find("yt-dlp"),["-I","-B",tools.VerifyFile("yt-dlp"),"--ignore-config","--no-plugin-dirs","--no-remote-components","--no-update","--no-playlist","--no-progress","--no-warnings","--no-cache-dir","--no-js-runtimes","--js-runtimes","node:"+tools.Find("node"),"--socket-timeout","20","--retries","0","--proxy",proxy.Address.AbsoluteUri,"--dump-single-json","--skip-download","--format","bestvideo+bestaudio/best","--","https://www.w3schools.com/html/html5_video.asp"],root,TimeSpan.FromMinutes(1),default);
 Console.WriteLine("Extractor exit: "+result.ExitCode+"; "+PrivacyText.Redact(result.Error));
 if(result.ExitCode==0) {using var parsed=JsonDocument.Parse(result.Output);var j=parsed.RootElement;Console.WriteLine("Extractor type: "+(j.TryGetProperty("_type",out var type)?type.GetString():"video")+"; entries: "+(j.TryGetProperty("entries",out var entries)?entries.GetArrayLength():0));}
}
else if(mode=="--cross-volume")
{
 var destination=Path.GetFullPath(args[2]);
 var sourceRoot=Path.Combine(Path.GetTempPath(),"mp4-vhd-"+Guid.NewGuid().ToString("N"));
 using var other=new JobStorage(sourceRoot);
 var job=other.CreateJob(Guid.NewGuid()); var source=Path.Combine(job,"fixture.mp4");
 try
 {
  if(JobStorage.VolumeId(job)==JobStorage.VolumeId(destination)) throw new InvalidOperationException("Test requires distinct actual volume IDs; subst does not qualify.");
  var bytes=new byte[4*1024*1024]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(source,bytes);
  var saved=await other.CommitAsync(source,destination,"cross-volume",default);
  if(!(await File.ReadAllBytesAsync(saved)).SequenceEqual(bytes) || Directory.EnumerateFiles(destination,"*.partial").Any()) throw new InvalidOperationException("Cross-volume integrity/cleanup failed.");
  await other.CleanupAsync(job); File.Delete(saved);
  Console.WriteLine("Distinct Windows volume IDs: verified; staged copy + SHA256 + rename + cleanup: passed. This does not assert a physical second drive.");
 }
 finally { await other.CleanupAsync(job); if(Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot,true); }
}
else if(mode=="--http")
{
 var fixture=Path.Combine(root,"input.mp4");
 var created=await runner.RunAsync(tools.Find("ffmpeg"),["-v","error","-nostdin","-y","-f","lavfi","-i","testsrc2=size=128x72:rate=12","-t","3","-c:v","libx264","-pix_fmt","yuv420p","-movflags","+faststart",fixture],root,TimeSpan.FromSeconds(30),default);
 if(created.ExitCode!=0) throw new InvalidOperationException("Fixture generation failed");
 var original=await File.ReadAllBytesAsync(fixture);
 var large=new byte[34*1024*1024]; original.CopyTo(large,0);
 BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(original.Length,4),(uint)(large.Length-original.Length)); "free"u8.CopyTo(large.AsSpan(original.Length+4));
 using var server=new TestServer(original,large);
 foreach(var scenario in new[]{"extensionless","redirect","disposition","query?test=1","range","no-range","chunked","wrong.webm","ambiguous","large"})
  await Download("HTTP "+scenario.Split('?')[0],new Uri(server.Base,scenario).AbsoluteUri);
}
else throw new ArgumentException("Unknown validation mode");
Console.WriteLine("Evidence directory: "+root);

sealed class Selection : ICandidateSelector
{ public Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates,CancellationToken ct)=>Task.FromResult(candidates[0]); } // Explicit first-candidate choice only in this diagnostic harness.
sealed class TestServer : IDisposable
{
 readonly HttpListener listener=new(); readonly byte[] small,large; public Uri Base {get;}
 public TestServer(byte[] small,byte[] large)
 {
  this.small=small; this.large=large; var picker=new TcpListener(IPAddress.Loopback,0); picker.Start();var port=((IPEndPoint)picker.LocalEndpoint).Port;picker.Stop();
  Base=new($"http://127.0.0.1:{port}/");listener.Prefixes.Add(Base.AbsoluteUri);listener.Start();_ = Serve();
 }
 async Task Serve()
 {
  try
  {
   while(listener.IsListening)
   {
    var context=await listener.GetContextAsync(); _ = Respond(context);
   }
  }
  catch(HttpListenerException){} catch(ObjectDisposedException){}
 }
 async Task Respond(HttpListenerContext c)
 {
  try
  {
   var scenario=c.Request.Url!.AbsolutePath; var data=scenario=="/large"?large:small;
   if(scenario=="/redirect"){c.Response.Redirect(new Uri(Base,"extensionless").AbsoluteUri);return;}
   c.Response.ContentType=scenario is "/ambiguous" or "/wrong.webm"?"application/octet-stream":"video/mp4";
   if(scenario=="/disposition") c.Response.Headers["Content-Disposition"]="attachment; filename=\"../../CON.mp4\"";
   long from=0,to=data.Length-1;
   c.Response.Headers["ETag"]="\"synthetic\"";
   if(scenario is "/range" or "/large" && c.Request.Headers["Range"] is { } range)
   {
    var bounds=range[6..].Split('-');from=long.Parse(bounds[0]);to=Math.Min(long.Parse(bounds[1]),data.Length-1);
    c.Response.StatusCode=206;c.Response.Headers["Content-Range"]=$"bytes {from}-{to}/{data.Length}";
   }
   if(scenario=="/chunked") c.Response.SendChunked=true; else c.Response.ContentLength64=to-from+1;
   await c.Response.OutputStream.WriteAsync(data.AsMemory((int)from,(int)(to-from+1)));
  }
  catch(Exception ex) when(ex is IOException or HttpListenerException or ObjectDisposedException){}
  finally{c.Response.Close();}
 }
 public void Dispose()=>listener.Close();
}
