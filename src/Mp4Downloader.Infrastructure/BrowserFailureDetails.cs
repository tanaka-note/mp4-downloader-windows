using Mp4Downloader.Core;

namespace Mp4Downloader.Infrastructure;

public enum BrowserPhase { Initialization, Navigation, Dom, Cookies, Candidates }

public static class BrowserFailureDetails
{
 // No exception message, URL, response body or credentials may enter this diagnostic.
 public static DownloadFailure From(BrowserPhase phase, Exception error)
 {
  var label = phase switch {
   BrowserPhase.Initialization => "ブラウザの初期化",
   BrowserPhase.Navigation => "ページへの接続",
   BrowserPhase.Dom => "ページ内の動画情報の読み取り",
   BrowserPhase.Cookies => "取得用のブラウザ情報の準備",
   _ => "検出した動画候補の解析"
  };
  var code = phase == BrowserPhase.Initialization ? FailureCode.Tool : FailureCode.Corrupt;
  return new(code, $"{label}に失敗しました（{phase} / {error.GetType().Name} / 0x{error.HResult:X8}）。");
 }
}
