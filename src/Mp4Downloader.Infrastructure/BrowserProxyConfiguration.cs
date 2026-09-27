namespace Mp4Downloader.Infrastructure;

public static class BrowserProxyConfiguration
{
 // Chromium accepts a proxy scheme/host/port, not a URL with a trailing path.
 public static string Arguments(Uri proxy)
 {
  if (proxy.Scheme != "http" || proxy.Host != "127.0.0.1" || proxy.Port <= 0 ||
      proxy.AbsolutePath != "/" || proxy.Query.Length != 0 || proxy.UserInfo.Length != 0 || proxy.Fragment.Length != 0)
   throw new ArgumentException("Expected the application's loopback HTTP proxy.", nameof(proxy));
  return $"--proxy-server={proxy.GetLeftPart(UriPartial.Authority)} --proxy-bypass-list=<-loopback>";
 }
}
