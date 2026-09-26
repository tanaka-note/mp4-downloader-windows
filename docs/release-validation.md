# v0.1.0 公開前検証記録

検証日: 2026-09-26。既存Draft PR #1、c4d0f02を起点とした最小限の安全性・配布検証改善。既存Downloader 1/2・T-ROOMへの変更なし。

## 実行した確認

| 対象 | 実行環境と結果 |
|---|---|
| 元の85テスト | ローカルWindows 11で全件再PASS |
| 追加後の124テスト | Core 33 / Integration 91、失敗0、skip0。Defender異常、private peer/proxy、悪意ある名前、junction、キャンセル、stage回収、cleanup例外を含む |
| 公開MP4 | W3Schools mov_bbb.mp4（query付き）、保存585,900 bytes、REMUX、実Defender Clean、Temp cleanup成功 |
| 公開HLS | Shaka公式 angel-one-hls/hls.m3u8、11,795,121 bytes、N_m3u8DL-RE経由、REMUX、実Defender Clean、保存・cleanup成功。フィルタproxy追加後も再確認 |
| 公開DASH | Shaka公式 angel-one/dash.mpd、12,251,629 bytes、N_m3u8DL-RE経由、REMUX、実Defender Clean、保存・cleanup成功。非暗号化static単一Period |
| 公開HTMLページ | W3Schools HTML videoページ。yt-dlpは2件のplaylistとして解析し、仕様通り一括取得を拒否。静的HTMLへfallbackし、診断用selectorの明示的な最初の候補選択で取得・Defender・保存成功。yt-dlp単独成功とは扱わない |
| 制御した実HTTP | loopback TCPサーバーで10ケース成功：拡張子なし、redirect、危険なContent-Disposition、query、Range、Rangeなし、chunked、拡張子不一致、曖昧なMIME、34MiB。公開サイト試験とは別区分 |
| MP4変換 | 実FFmpeg/ffprobeでMP4/MKV/TS/MOV/WebM/WMV、片側変換、全変換、silent video、破損/HDR/audio-only停止を自動テスト |
| ローカルpublish起動 | 通常権限のデスクトップでWinUI起動、動的WebView2検出、ブラウザproxy経由のprivate peer接続拒否、欠落Runtime指定の初期化失敗、Direct取得、実Defender Clean、保存・Temp cleanup成功 |
| 署名 | CurrentUser/LocalMachineのコード署名証明書を確認、利用可能な証明書なし。アプリEXEはNotSigned |

公開素材は少数回の代表確認に限定し、CIでは繰り返し取得しない。URL全文・Cookie・Authorization・tokenを一般ログや履歴へ保存しない。検証用の固定公開URLと実行手順はvalidation harnessにある。元の素材は同梱配布しない。

## CIと配布方式

CI結果の正本は[PR #1のchecks](https://github.com/tanaka-note/mp4-downloader-windows/pull/1/checks)。今回、restore/build/tests/security regression/publish/ZIP全ファイルhash検証/展開ZIP起動/VHD別ボリュームを独立step化した。結果は最終報告で確定する。

全publishフォルダをZIP配布。.NET・Windows App SDKはself-contained、win-x64。SingleFile/trimming/ReadyToRunはfalse。WebView2 Evergreenは別途必要。EXE単体配布ではない。内部検証ZIP名は`Mp4Downloader-v0.1.0-win-x64-INTERNAL-VALIDATION.zip`。公開許可が出た場合のみ`-ForRelease`で公開名を生成できる。ZIP各ファイルのSHA256をpublish元と比較し、ZIP自体のSHA256も生成する。

Windows hosted CIが管理者の場合、WinUI起動・Runtime環境作成・欠落Runtimeエラー・安全なsave/cleanupを実行し、ブラウザ再生はskipと明記する。ローカルの通常権限でブラウザ再生検出を確認する。hosted CIがDefenderを使えない場合、検査不能・保存なし・Temp cleanupを確認するため、実Defender Cleanを確認したとは報告しない。

VHD試験では新規128MiB VHDを専用ファイルに作成し、他のドライブを操作しない。実Windows volume IDが異なることをassertしてcopy/hash/rename/cleanupを確認する。substは利用しない。物理的な第2ドライブの確認とは区別する。

## セキュリティ判断と未確認

shellを使用しないArgumentList、ローカルFFmpeg input/protocol制限、HTTP redirect時のorigin制限、credential履歴のallowlist、no-overwrite rename、stage SHA256、private-IP接続拒否、外部Engine/browser用のTLSを復号しないフィルタproxyを実装。Tool固定hash、bounded output/deadline、Windows Job Object、キャンセルを確認。保存先reparse ancestorsを拒否し、cleanupはchild junctionを辿らない。

これは完全なOS sandboxではない。外部media parser自身の脆弱性をDefenderの最終ファイル検査が防ぐという保証はない。Tempは通常ユーザーのLocalAppData権限を利用し、同一ユーザーの悪意ある別プロセスや管理者からの改変防止を保証しない。Defender無効・missing・service不可・timeout・malware等は合成結果とhash改変による異常試験であり、実OSのDefender設定を停止した試験ではない。third-party AVの実機、物理第2ドライブ、専用Windows 11の初回セットアップ、署名後のSmartScreen挙動は未確認。

## ライセンスgateの解消と最終判定

旧Gyan FFmpegとPyInstaller版yt-dlpを配布対象から外した。FFmpeg 9.0.2は固定FFmpeg/x264/libvpx/Opusソースからビルドし、Windowsの実`-version`／`-buildconf`／`-L`、元ソースhash、compiler/configuration、licenceを記録する。yt-dlpは公式zipimport 2026.08.19＋CPython 3.14.7＋Node.js v24.21.0へ変更し、原配布hashとlicenseを保存。N_m3u8DL-REも固定tagから.NET 10.0.12・locked dependenciesでビルドした。

アプリソースはMIT、独立したGPLツールとMicrosoft配布DLLの条件は別々に保持する。`THIRD_PARTY_TERMS.md`にMicrosoft条件を渡し、全DLL/実行ファイルの原パッケージ照合を`Publish-Inventory.ps1`で実施する。対応ソース、build scripts／設定／lockfile／compiler／patch情報（媒体ソース変更なし）は同じReleaseのsource ZIPへ保存する。ライセンスgateは実binary hash／version／禁止構成／追加library／source／notice／build record／Release asset／SHA256SUMSを検査する。

バイナリ変更後の最終試験結果、PR/main SHA、CI、アップロードしたassetの再取得・起動検証は最終報告およびGitHub Releaseに記録する。Windows CIが管理者の場合のブラウザskipと、通常権限で実行したWebView2試験は引き続き区別する。物理第2ドライブ／third-party AV／初期状態Windowsは未確認のままである。
## 最終候補の再検証（ライセンス対応後）

FFmpeg 9.0.2固定自前ビルドへの置換後、Core 33 / Integration 93の126件PASS、FAIL 0 / SKIP 0。ライセンスgate PASS、危険な構成・改変・source/notice/asset不足を拒否する9件PASS。371の配布実行ファイル・ライブラリをhashと原パッケージで照合した。実HTTP 10ケース、公開MP4/HLS/DASH、yt-dlp generic Directの取得・実Defender Clean・保存・cleanupを再確認。通常権限の新規publishでWinUI起動、WebView2動的検出、private-peer拒否、FFmpeg、yt-dlp、Defender Clean、保存・cleanupがPASS。ブラウザRuntimeは153.0.4234.48。対応ソース10archivesと42 runtime file pinsを照合した。main/Releaseのclean buildとアップロードassetの最終結果はGitHub checksとReleaseへ記録する。
