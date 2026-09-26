namespace Mp4Downloader.Core;

public sealed class DownloadCoordinator(IReadOnlyList<IResolver> resolvers, ICandidateSelector selector,
 IAcquisitionPlanner planner, IReadOnlyList<IDownloadEngine> engines, IMediaPipeline media, IScanner scanner, IJobStorage storage)
{
 private readonly SemaphoreSlim gate = new(1, 1);
 public async Task<JobOutcome> RunAsync(string url, string destination, IProgress<JobProgress> progress, CancellationToken ct)
 {
  if (!await gate.WaitAsync(0, ct)) throw new DownloadFailure(FailureCode.Unsupported, "別のダウンロードを実行中です。");
  string? directory = null;
  Uri? page = null;
  PlaybackCandidate? candidate = null;
  string engine = "", mode = "";
  JobOutcome outcome;
  try
  {
   page = UrlPolicy.Parse(url);
   directory = storage.CreateJob(Guid.NewGuid());
   storage.EnsureSpace(directory, destination, 128L * 1024 * 1024);
   progress.Report(new(JobStage.Resolving, "動画を解析しています"));
   AcquisitionResult? acquired = null;
   foreach (var resolver in resolvers)
   {
    ct.ThrowIfCancellationRequested();
    IReadOnlyList<PlaybackCandidate> found;
    try { found = await resolver.ResolveAsync(page, ct); }
    catch (DownloadFailure ex) when (ex.CanFallback || ex.Code == FailureCode.AccessRequired) { continue; }
    var viable = found.Where(c => c.Viable).ToList();
    if (viable.Count == 0) continue;
    progress.Report(new(JobStage.Selecting, "動画候補を確認しています"));
    candidate = viable.Count == 1 ? viable[0] : await selector.SelectAsync(viable, ct);
    CheckProtection(candidate);
    for (var refreshAttempt = 0; refreshAttempt < 2; refreshAttempt++)
    {
     var refreshRequested = false;
     foreach (var plan in planner.Plan(candidate))
     {
     var downloader = engines.Single(e => e.Name == plan.Engine);
     var attempt = Path.Combine(directory, Guid.NewGuid().ToString("N"));
     Directory.CreateDirectory(attempt);
     progress.Report(new(JobStage.Downloading, "動画を取得しています"));
     try
     {
      acquired = await downloader.DownloadAsync(plan, attempt, progress, ct);
      if (!acquired.Complete || acquired.Files.Count == 0) throw new DownloadFailure(FailureCode.Corrupt, "動画の取得が完了していません。");
      engine = acquired.Engine;
      break;
     }
     catch (DownloadFailure ex) when (ex.Code == FailureCode.AccessRequired && candidate.Refreshable && refreshAttempt == 0)
     { await storage.CleanupAsync(attempt); refreshRequested = true; break; }
     catch (DownloadFailure ex) when (ex.CanFallback) { await storage.CleanupAsync(attempt); }
     }
     if (acquired is not null || !refreshRequested) break;
     progress.Report(new(JobStage.Resolving, "動画URLを更新しています"));
     var refreshed = (await resolver.ResolveAsync(page, ct)).FirstOrDefault(c => c.Id == candidate.Id && c.Viable);
     if (refreshed is null) throw new DownloadFailure(FailureCode.AccessRequired, "同じ動画の取得URLを更新できませんでした。");
     CheckProtection(refreshed);
     if (candidate.Duration is > 0 and var before && refreshed.Duration is > 0 and var after && Math.Abs(before - after) > Math.Max(2, before * 0.02))
      throw new DownloadFailure(FailureCode.Corrupt, "再解析した動画が元の動画と一致しません。");
     candidate = refreshed;
    }
    if (acquired is not null) break;
   }
   if (acquired is null || candidate is null) throw new DownloadFailure(FailureCode.NoMatch, "取得できる非DRM動画を特定できませんでした。");
   progress.Report(new(JobStage.Probing, "動画の形式を確認しています"));
   var normalized = await media.NormalizeAsync(acquired, directory, progress, ct);
   mode = normalized.Plan.Mode.ToString();
   storage.EnsureSpace(directory, destination, normalized.Media.Size + 16L * 1024 * 1024);
   progress.Report(new(JobStage.Scanning, "Windows Defenderで検査しています"));
   var scan = await scanner.ScanAsync(normalized.Path, ct);
   if (scan != ScanVerdict.Clean)
    throw new DownloadFailure(FailureCode.Scan, scan == ScanVerdict.ThreatDetected ? "脅威検出：保存を中止しました。" : "検査不能：Windows Defenderによる検査を完了できないため保存を中止しました。");
   ct.ThrowIfCancellationRequested();
   progress.Report(new(JobStage.Saving, "MP4を保存しています"));
   var saved = await storage.CommitAsync(normalized.Path, destination, candidate.Title, ct);
   // Saving is the commit point: history failure must never turn a saved video into a failed download.
   var notice = "検査済み：MP4を保存しました。";
   try
   {
    await storage.RecordAsync(new(candidate.Title, page.IdnHost, Path.GetFileName(saved), normalized.Media.Size,
     normalized.Media.Video!.Width, normalized.Media.Video.Height, normalized.Media.Video.Duration,
     engine, mode, "成功", DateTimeOffset.Now, saved), CancellationToken.None);
   }
   catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice += " 履歴の記録に失敗しました。"; }
   outcome = new(JobStage.Completed, notice, saved);
  }
  catch (OperationCanceledException) { outcome = new(JobStage.Cancelled, "中止しました。"); }
  catch (DownloadFailure ex) { outcome = new(JobStage.Failed, ex.Message); }
  catch (IOException) { outcome = new(JobStage.Failed, "ファイル操作に失敗しました。空き容量とアクセス権を確認してください。"); }
  catch (UnauthorizedAccessException) { outcome = new(JobStage.Failed, "保存先または作業領域にアクセスできません。"); }
  catch (Exception) { outcome = new(JobStage.Failed, "処理を完了できませんでした。機密情報を含む詳細は記録していません。"); }
  finally
  {
   try { if (directory is not null) await storage.CleanupAsync(directory); }
   catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Recovery retries on next startup; preserve the job outcome. */ }
   finally { gate.Release(); }
  }
  if (outcome.Stage != JobStage.Completed && page is not null)
  {
   try { await storage.RecordAsync(new("動画", page.IdnHost, "", 0, 0, 0, 0, engine, mode, outcome.Stage == JobStage.Cancelled ? "中止" : "失敗", DateTimeOffset.Now, null), CancellationToken.None); }
   catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
  }
  progress.Report(new(outcome.Stage, outcome.Message, outcome.Stage == JobStage.Completed ? 1 : null));
  return outcome;
 }
 public static void CheckProtection(PlaybackCandidate candidate)
 {
  if (candidate.Protection == Protection.Drm) throw new DownloadFailure(FailureCode.Drm, "この動画はDRMで保護されているためダウンロードできません。");
  if (candidate.Protection == Protection.Encrypted) throw new DownloadFailure(FailureCode.Encrypted, "この暗号化方式には現在対応していません。");
  if (candidate.Protection != Protection.Clear) throw new DownloadFailure(FailureCode.Unsupported, "動画の保護状態を確認できませんでした。");
 }
}
