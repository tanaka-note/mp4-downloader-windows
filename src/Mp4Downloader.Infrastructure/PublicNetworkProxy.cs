using System.Net;
using System.Net.Sockets;
using System.Text;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

// Short-lived loopback proxy. It never logs request headers/URLs or intercepts TLS.
// CONNECT peers are checked at actual socket connection; plain HTTP closes after one request.
public sealed class PublicNetworkProxy : IDisposable
{
 private readonly TcpListener listener = new(IPAddress.Loopback,0);
 private readonly CancellationTokenSource stop = new();
 private readonly SemaphoreSlim clients = new(16,16);
 public Uri Address { get; }
 private long requests;
 public long RequestCount => Interlocked.Read(ref requests);
 public PublicNetworkProxy()
 {
  listener.Start(); Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"); _ = AcceptAsync();
 }
 private async Task AcceptAsync()
 {
  try
  {
   while(!stop.IsCancellationRequested)
   {
    var client=await listener.AcceptTcpClientAsync(stop.Token);
    if(!clients.Wait(0)) {client.Dispose();continue;}
    _ = HandleAsync(client);
   }
  }
  catch(Exception ex) when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
 }
 private async Task HandleAsync(TcpClient client)
 {
  using(client)
  {
   var inbound=client.GetStream();
   try
   {
    using var headerDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);headerDeadline.CancelAfter(TimeSpan.FromSeconds(15));
    var bytes=new List<byte>(); var one=new byte[1];
    while(bytes.Count<16384)
    {
     if(await inbound.ReadAsync(one,headerDeadline.Token)==0) return;
     bytes.Add(one[0]);
     if(bytes.Count>=4 && bytes.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10})) break;
    }
    if(bytes.Count>=16384) return;
    var lines=Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");var first=lines[0].Split(' ');
    if(first.Length!=3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0")) return;
    Interlocked.Increment(ref requests);
    var tunnel=first[0]=="CONNECT";
    var target=UrlPolicy.Parse(tunnel?"https://"+first[1]:first[1]);
    if(target.Port is not (80 or 443) || !tunnel && target.Scheme!="http") throw new DownloadFailure(FailureCode.Unsupported,"Unsupported proxy target");
    await using var outbound=await NetworkPolicy.OpenPublicAsync(target.IdnHost,target.Port,headerDeadline.Token);
    if(tunnel) await inbound.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),stop.Token);
    else
    {
     var headers=lines.Skip(1).Where(l=>l.Length>0 && !l.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Proxy-",StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Host:",StringComparison.OrdinalIgnoreCase));
     var rewritten=$"{first[0]} {target.PathAndQuery} HTTP/1.1\r\nHost: {target.Authority}\r\nConnection: close\r\n"+string.Join("\r\n",headers)+"\r\n\r\n";
     await outbound.WriteAsync(Encoding.ASCII.GetBytes(rewritten),stop.Token);
    }
    using var relayStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var upstream=inbound.CopyToAsync(outbound,relayStop.Token);var downstream=outbound.CopyToAsync(inbound,relayStop.Token);
    await Task.WhenAny(upstream,downstream);relayStop.Cancel();
    try {await Task.WhenAll(upstream,downstream);} catch(OperationCanceledException){}
   }
   catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or DownloadFailure or ObjectDisposedException)
   {try {await inbound.WriteAsync("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray());}catch(Exception writeError) when(writeError is IOException or ObjectDisposedException){} }
   finally {clients.Release();}
  }
 }
 public void Dispose() {stop.Cancel();listener.Stop();}
}
