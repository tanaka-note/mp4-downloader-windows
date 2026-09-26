# MP4 Downloader for Windows

Windows 11 x64用のローカル動画ダウンローダー。URLの解析、取得、MP4変換、Microsoft Defender検査、保存をPC内で行います。既存Downloader 1/2・T-ROOMとは独立しています。アプリ独自のサーバー、有料API、テレメトリはありません。WindowsやWebView2の通信はOS設定に従います。

現在はv0.1.0の公開前検証段階です。対応ソース・ライセンスのRelease gateが未完了のため、公開用バイナリReleaseは作成していません。

## 使い方と配布形式

ZIPをフォルダへ完全に展開し、フォルダ内の`Mp4Downloader.App.exe`を通常のユーザー権限で起動します。EXE単体をコピーしても動作しません。URLを貼り付けて「ダウンロード」を押します。初期保存先はWindowsの「ダウンロード」フォルダで、変更可能です。同名ファイルを上書きしません。作業ファイルはLocalAppDataに置き、完了・失敗・中止時に削除します。

publishフォルダ一式をZIP配布する方式です。.NETとWindows App SDKはself-contained、RIDはwin-x64。SingleFile、trimming、ReadyToRunは無効です。WebView2 Evergreen Runtimeは別途必要で、同梱・自動インストールしません。Runtimeを利用できなければ、設定の[Microsoft公式導入案内](https://developer.microsoft.com/microsoft-edge/webview2/)を確認できます。

コード署名はありません。正式な署名証明書は確認できていません。公開後のダウンロードにはWindows SmartScreen等の警告が出る可能性があります。将来、正式なコード署名を導入します。

## 対応範囲

- Direct HTTP/HTTPS動画、拡張子なし、Range取得とSingle Streamへのフォールバック。
- 非暗号化VOD HLS、単一Periodの非暗号化static DASH。公開StreamingはN_m3u8DL-RE（固定beta版）、単純なHLSは内蔵Engineでも取得します。
- yt-dlp+Denoによるサイト解析、専用WebView2プロファイルによる動的URL検出。
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
pwsh ./tools/Package.ps1
```

PowerShell 7がない場合、Install/Publish/Smokeは`powershell -ExecutionPolicy Bypass -File`でも実行できます。PackageはPowerShell 7が必要です。公開テスト素材試験は`dotnet run --project tools/Mp4Downloader.Validation -- --public .`、制御したHTTP試験は`--http`です。外部サイト試験をCIで毎回実行しません。

CIではrestore、build、全テスト、セキュリティ回帰、publish、ZIP全ファイル照合、展開ZIP起動、保存失敗時の安全性、VHDによる別ボリューム保存を独立stepで確認します。管理者として動くCIではブラウザ再生をskipし、その事実を記録します。通常権限のローカルデスクトップでは動的ブラウザ検出も確認します。CIのZIPは内部検証用です。

## ライセンスと公開条件

ソースはGPL-3.0-or-later。[LICENSE](LICENSE)、[third-party notices](THIRD_PARTY_NOTICES.md)、[同梱notice](licenses/)、[NuGet依存一覧](docs/nuget-dependency-inventory.json)を参照してください。外部ツールのVersion・取得元・SHA256は`tools/tools.lock.json`で固定しています。

GPLのFFmpeg/yt-dlpについて、正確な対応ソース・依存Version・ビルド手順が未整備です。`tools/release-policy.json`はpublicRedistributionApproved=falseで、`Package.ps1 -ForRelease`は失敗します。notice追加や別プロセス化だけで配布条件を満たしたとは扱いません。[architecture](docs/architecture.md)と[公開前検証記録](docs/release-validation.md)を参照してください。
