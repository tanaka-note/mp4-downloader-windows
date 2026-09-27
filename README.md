# MP4 Downloader for Windows

Windows 11 x64用のローカル動画ダウンローダー。URLの解析、取得、MP4変換、Microsoft Defender検査、保存をPC内で行います。既存Downloader 1/2・T-ROOMとは独立しています。アプリ独自のサーバー、有料API、テレメトリはありません。WindowsやWebView2の通信はOS設定に従います。

v0.1.1の配布先は[GitHub Release](https://github.com/tanaka-note/mp4-downloader-windows/releases/tag/v0.1.1)です。Windows本体ZIP、SHA256SUMS、対応ソースZIP、ライセンス本文を同じReleaseから取得してください。

## 使い方と配布形式

ZIPをフォルダへ完全に展開し、フォルダ内の`Mp4Downloader.App.exe`を通常のユーザー権限で起動します。EXE単体をコピーしても動作しません。URLを貼り付けて「ダウンロード」を押します。初期保存先はWindowsの「ダウンロード」フォルダで、変更可能です。同名ファイルを上書きしません。作業ファイルはLocalAppDataに置き、完了・失敗・中止時に削除します。

publishフォルダ一式をZIP配布する方式です。.NETとWindows App SDKはself-contained、RIDはwin-x64。SingleFile、trimming、ReadyToRunは無効です。WebView2 Evergreen Runtimeは別途必要で、同梱・自動インストールしません。Runtimeを利用できなければ、設定の[Microsoft公式導入案内](https://developer.microsoft.com/microsoft-edge/webview2/)を確認できます。

コード署名はありません。正式な署名証明書は確認できていません。公開後のダウンロードにはWindows SmartScreen等の警告が出る可能性があります。将来、正式なコード署名を導入します。

## 対応範囲

- Direct HTTP/HTTPS動画、拡張子なし、Range取得とSingle Streamへのフォールバック。
- 非暗号化VOD HLS、単一Periodの非暗号化static DASH。公開StreamingはN_m3u8DL-RE（固定beta版）、単純なHLSは内蔵Engineでも取得します。
- yt-dlp公式zipimport＋同梱Python／Node.jsによるサイト解析、専用WebView2プロファイルによる動的URL検出。ブラウザのCookieストアを読み取りません。
- ffprobeで実コンテナ・Codecを確認し、MP4/H.264 8bit yuv420p/AAC-LC/faststartへ正規化。PASS_THROUGH、REMUX、片方だけの変換、全変換から最小の処理を選びます。MKV、TS、MOV、WebM、WMV等は対応Codec範囲内で変換できます。
- Microsoft Defenderの確認済みCleanのみ保存。「脅威検出」「検査不能」を区別し、検査不能時も保存しません。third-party antivirusのみの環境では保存できない場合があります。

ログイン・Cookie・DRM等を必要とする一部ストリームは未対応です。認証付きDASH、機密URLを安全に渡せないDASH、DRM、暗号化HLS、live/LL-HLS、音声のみ、HDR、上限を超える解像度、一括取得は対象外です。localhost/private network、UNC、junction/symlinkを含む保存先も対象外です。サイトの変更で解析できなくなる場合があります。アクセス権のある素材にのみ利用してください。

## 開発・検証

Windows 11、.NET SDK 10.0.401で実行します。Visual Studioや外部ツールのPATH登録は不要です。

```powershell
pwsh ./tools/Install-Tools.ps1
dotnet restore MP4Downloader.slnx -p:Platform=x64 --locked-mode
dotnet build MP4Downloader.slnx -c Release -p:Platform=x64 --no-restore
dotnet test MP4Downloader.slnx -c Release -p:Platform=x64 --no-build
pwsh ./tools/Publish.ps1
pwsh ./tools/Smoke-Test.ps1
pwsh ./tools/Package-Sources.ps1
pwsh ./tools/Test-LicenseGate.Tests.ps1
pwsh ./tools/Package.ps1 -ForRelease
pwsh ./tools/Release-Assets.ps1
```

PowerShell 7がない場合、Install/Publish/Smokeは`powershell -ExecutionPolicy Bypass -File`でも実行できます。PackageはPowerShell 7が必要です。公開テスト素材試験は`dotnet run --project tools/Mp4Downloader.Validation -- --public .`、制御したHTTP試験は`--http`です。外部サイト試験をCIで毎回実行しません。

CIではrestore、build、全テスト、セキュリティ回帰、publish、対応ソース・ライセンス回帰、Release asset/hash検査、ZIP全ファイル照合、展開ZIP起動、VHDによる別ボリューム保存を独立stepで確認します。管理者CIではブラウザ再生をskipと記録し、通常権限のローカルでは動的検出とprivate接続拒否を確認します。tag workflowはDraft Releaseだけを作成し、公開前にアップロードした成果物そのものを再取得して検証します。

## ライセンスと公開条件

アプリ自身のソースはMIT。独立したFFmpegはGPLv3+、yt-dlp zipimportはUnlicense／ISC／MIT、Microsoft配布DLLは元のMicrosoft条件です。[LICENSE](LICENSE)、[third-party notices](THIRD_PARTY_NOTICES.md)、[Microsoft等の配布条件](THIRD_PARTY_TERMS.md)、[同梱notice](licenses/)を参照してください。Microsoftコンポーネントの利用・再配布には、その元の条件への同意が必要です。ツールのVersion・取得元・SHA256は`tools/tools.lock.json`で固定しています。

対応ソース・正確なcommit/hash・ビルド手順・設定・lockfileを`Mp4Downloader-v0.1.1-third-party-sources.zip`に保存します。FFmpegはx264／libvpx／Opusのみを追加した自前ビルドで、nonfreeを含みません。配布ZIP内の`THIRD_PARTY_MANIFEST.json`は全実行ファイル／DLLの由来とhashを記録します。source・notice・hash・構成・assetが不足するとRelease gateが失敗します。[ビルド記録](docs/third-party-builds.md)、[architecture](docs/architecture.md)、[検証記録](docs/release-validation.md)を参照してください。
