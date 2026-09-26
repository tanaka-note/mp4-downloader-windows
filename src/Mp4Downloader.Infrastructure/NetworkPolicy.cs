using System.Net;
using System.Net.Sockets;
using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public static class NetworkPolicy
{
 public static bool IsPublic(IPAddress address)
 {
  if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
  if(IPAddress.IsLoopback(address)) return false;
  var b=address.GetAddressBytes();
  if(address.AddressFamily==AddressFamily.InterNetwork)
   return !(b[0] is 0 or 10 or 127 || b[0]>=224 || b[0]==169 && b[1]==254 || b[0]==172 && b[1] is >=16 and <=31 || b[0]==192 && (b[1] is 168 or 0 || b[1]==88 && b[2]==99) || b[0]==100 && b[1] is >=64 and <=127 || b[0]==198 && (b[1] is 18 or 19 || b[1]==51 && b[2]==100) || b[0]==203 && b[1]==0 && b[2]==113);
  // Only global-unicast IPv6; exclude mapped, ULA, link-local, multicast and documentation prefix.
  return (b[0]&0xe0)==0x20 && !(b[0]==0x20 && b[1]==1 && b[2]==0x0d && b[3]==0xb8);
 }
 public static async Task<IPAddress[]> PublicAddressesAsync(string host,CancellationToken ct)
 {
  IPAddress[] addresses;
  try { addresses=await Dns.GetHostAddressesAsync(host,ct); }
  catch(SocketException) { throw new DownloadFailure(FailureCode.Network,"接続先を確認できませんでした。"); }
  if(addresses.Length==0 || addresses.Any(a=>!IsPublic(a))) throw new DownloadFailure(FailureCode.Unsupported,"localhost・プライベートネットワークの動画取得には対応していません。");
  return addresses;
 }
 public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context,CancellationToken ct)
  => await OpenPublicAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
 public static async Task<Stream> OpenPublicAsync(string host,int port,CancellationToken ct)
 {
  var addresses=await PublicAddressesAsync(host,ct);
  foreach(var address in addresses)
  {
   var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
   try { await socket.ConnectAsync(new IPEndPoint(address,port),ct); return new NetworkStream(socket,true); }
   catch(SocketException) { socket.Dispose(); }
   catch { socket.Dispose(); throw; }
  }
  throw new DownloadFailure(FailureCode.Network,"公開動画サーバーへ接続できませんでした。");
 }
}
