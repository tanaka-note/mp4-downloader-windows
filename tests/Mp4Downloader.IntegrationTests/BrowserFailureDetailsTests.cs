using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using Xunit;

public class BrowserFailureDetailsTests
{
 [Theory]
 [InlineData(BrowserPhase.Initialization, FailureCode.Tool)]
 [InlineData(BrowserPhase.Dom, FailureCode.Corrupt)]
 [InlineData(BrowserPhase.Cookies, FailureCode.Corrupt)]
 [InlineData(BrowserPhase.Candidates, FailureCode.Corrupt)]
 public void StageIsPreservedWithoutLeakingExceptionMessage(BrowserPhase phase, FailureCode code)
 {
  var failure = BrowserFailureDetails.From(phase, new InvalidOperationException("https://secret.invalid/?token=private Cookie=private"));
  Assert.Equal(code, failure.Code);
  Assert.Contains(phase.ToString(), failure.Message);
  Assert.Contains("InvalidOperationException", failure.Message);
  Assert.DoesNotContain("secret.invalid", failure.Message);
  Assert.DoesNotContain("private", failure.Message);
  Assert.DoesNotContain("Runtime", failure.Message);
 }
}
