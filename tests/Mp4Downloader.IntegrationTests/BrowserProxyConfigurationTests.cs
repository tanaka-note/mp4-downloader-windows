using Mp4Downloader.Infrastructure;
using Xunit;

public class BrowserProxyConfigurationTests
{
 [Fact] public void ProxyUsesAuthorityWithoutUrlPath()
 {
  using var proxy = new PublicNetworkProxy();
  Assert.EndsWith("/", proxy.Address.AbsoluteUri);
  Assert.Equal($"--proxy-server=http://127.0.0.1:{proxy.Address.Port} --proxy-bypass-list=<-loopback>", BrowserProxyConfiguration.Arguments(proxy.Address));
 }
 [Theory]
 [InlineData("https://127.0.0.1:1234/")]
 [InlineData("http://example.com:1234/")]
 [InlineData("http://127.0.0.1:1234/path")]
 [InlineData("http://127.0.0.1:1234/?credential=value")]
 public void RejectUnexpectedProxyAddress(string value) => Assert.Throws<ArgumentException>(() => BrowserProxyConfiguration.Arguments(new Uri(value)));
}
