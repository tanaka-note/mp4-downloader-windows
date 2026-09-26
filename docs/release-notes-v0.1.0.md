Windows 11 x64向けの完全ローカル動画ダウンローダーです。Windows ZIPを全展開し、通常権限でMp4Downloader.App.exeを起動してください。EXE単体配布ではありません。WebView2 Evergreen Runtimeが別途必要です。

- Direct／Range fallback、非暗号化VOD HLS／単一Period static DASH、yt-dlp／HTML／WebView2解析。
- 実ファイル解析から再エンコードを最小限にし、H.264／AAC MP4へ正規化。
- Defenderが検査済みCleanの場合のみ保存。脅威検出／検査不能では保存しません。
- 同名上書き防止、別ボリュームpartialコピー・hash照合・rename、cancel／crash cleanup。
- unsignedです。SmartScreen警告の可能性があります。正式コード署名はありません。

DRM、暗号化HLS、認証付きDASH、一部ログイン／Cookie依存、live、HDR、音声のみ、一括取得は未対応です。DRM回避機能はありません。TLS impersonation用のcurl_cffiは同梱せず、HTML／WebView2 fallbackを利用します。private network／UNC／junction保存先も対象外です。

アプリソースはMIT、独立したFFmpeg 9.0.2＋x264はGPLv3+、libvpx／OpusはBSD、yt-dlp 2026.08.19公式zipimportはUnlicense／ISC／MITです。Python／Node.jsとMicrosoftランタイムの元の条件は添付notice／licensesを参照してください。Microsoft配布コンポーネントの利用・再配布には元の条件が適用されます。

同じReleaseのthird-party-sources.zipに対応ソース、固定hash／commit、build scripts／設定／lockfiles／licensesを保存しています。SHA256SUMSで取得したassetを照合してください。既存Downloader 1／2、T-ROOM、Cloudflareへの依存・変更はありません。

自動試験と実Defender／公開MP4・HLS・DASH、通常権限WebView2を確認。VHDは実別ボリュームとして検証しましたが、物理第2ドライブ、third-party AV実機、専用Windows初期状態は未確認です。管理者CIではブラウザ再生をskipし、通常権限ローカルで確認します。
