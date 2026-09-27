using Mp4Downloader.Core;
using Xunit;
namespace Mp4Downloader.Core.Tests;

public class CoordinatorTests
{
 [Theory]
 [InlineData(ScanVerdict.Clean, JobStage.Completed, 1)]
 [InlineData(ScanVerdict.ThreatDetected, JobStage.Failed, 0)]
 [InlineData(ScanVerdict.ScanUnavailable, JobStage.Failed, 0)]
 [InlineData(ScanVerdict.ScanError, JobStage.Failed, 0)]
 public async Task OnlyVerifiedCleanCanCommit(ScanVerdict verdict, JobStage expected, int commits)
 {
  var storage = new FakeStorage();
  var coordinator = Build(storage, verdict);
  var outcome = await coordinator.RunAsync("https://example.invalid/watch?fixture=private", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(expected, outcome.Stage); Assert.Equal(commits, storage.Commits); Assert.True(storage.Cleaned);
  Assert.All(storage.Entries, entry => Assert.DoesNotContain("fixture=private", entry.Host + entry.Title));
 }
 [Fact] public async Task HistoryFailureDoesNotUndoSave()
 {
  var storage = new FakeStorage { FailHistory = true };
  var outcome = await Build(storage, ScanVerdict.Clean).RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(JobStage.Completed, outcome.Stage); Assert.Equal(1, storage.Commits); Assert.True(storage.Cleaned);
 }
 [Theory]
 [InlineData(ScanVerdict.Clean, JobStage.Completed)]
 [InlineData(ScanVerdict.ScanUnavailable, JobStage.Failed)]
 public async Task CleanupFailurePreservesOutcomeAndReleasesJobGate(ScanVerdict verdict, JobStage expected)
 {
  var storage = new FakeStorage { FailCleanup = true };
  var coordinator = Build(storage, verdict);
  try
  {
   var first = await coordinator.RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
   var second = await coordinator.RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
   Assert.Equal(expected, first.Stage); Assert.Equal(expected, second.Stage);
   if (verdict != ScanVerdict.Clean) Assert.Contains("検査不能", first.Message);
  }
  finally { if (Directory.Exists(storage.Root)) Directory.Delete(storage.Root, true); }
 }
 [Theory]
 [InlineData(Protection.Drm)] [InlineData(Protection.Encrypted)] [InlineData(Protection.Unknown)]
 public async Task ProtectionStopsBeforeAcquire(Protection protection)
 {
  var storage = new FakeStorage(); var engine = new FakeEngine();
  var outcome = await Build(storage, ScanVerdict.Clean, protection, engine).RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(JobStage.Failed, outcome.Stage); Assert.Equal(0, engine.Calls); Assert.Equal(0, storage.Commits); Assert.True(storage.Cleaned);
 }
 [Fact] public async Task CancellationNeverSaves()
 {
  var storage = new FakeStorage(); var engine = new FakeEngine { Cancel = true };
  var outcome = await Build(storage, ScanVerdict.Clean, Protection.Clear, engine).RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(JobStage.Cancelled, outcome.Stage); Assert.True(storage.Cleaned); Assert.Equal(0, storage.Commits);
 }
 private static DownloadCoordinator Build(FakeStorage storage, ScanVerdict verdict, Protection protection = Protection.Clear, FakeEngine? engine = null) =>
  new([new FakeResolver(protection)], new FakeSelector(), new FakePlanner(), [engine ?? new FakeEngine()], new FakeMedia(), new FakeScanner(verdict), storage);
 [Fact] public async Task FinalBrowserFailureRemainsVisibleAndRecordsOnlyTypedCode()
 {
  var storage = new FakeStorage();
  var coordinator = new DownloadCoordinator([new FailedResolver(FailureCode.NoMatch), new FailedResolver(FailureCode.Network)],
   new FakeSelector(), new FakePlanner(), [new FakeEngine()], new FakeMedia(), new FakeScanner(ScanVerdict.Clean), storage);
  var result = await coordinator.RunAsync("https://example.invalid/?signature=secret", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(JobStage.Failed, result.Stage);
  Assert.Equal(FailureCode.Network, result.ErrorCode);
  Assert.Equal("fixture Network", result.Message);
  Assert.Equal(FailureCode.Network, storage.Entries.Single().ErrorCode);
  Assert.Equal("example.invalid", storage.Entries.Single().Host);
  Assert.True(storage.Cleaned);
 }
 [Fact] public async Task FailedResolverDoesNotPreventLaterSuccess()
 {
  var storage = new FakeStorage();
  var coordinator = new DownloadCoordinator([new FailedResolver(FailureCode.Network), new FakeResolver(Protection.Clear)],
   new FakeSelector(), new FakePlanner(), [new FakeEngine()], new FakeMedia(), new FakeScanner(ScanVerdict.Clean), storage);
  var result = await coordinator.RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(JobStage.Completed, result.Stage); Assert.Null(result.ErrorCode);
 }
 private sealed class FailedResolver(FailureCode code) : IResolver
 {
  public string Name => "fixture failed";
  public Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct) => throw new DownloadFailure(code, "fixture " + code);
 }
 [Theory]
 [InlineData(false, JobStage.Completed)]
 [InlineData(true, JobStage.Failed)]
 public async Task ExpiredUrlRefreshRequiresSameVideo(bool changed, JobStage expected)
 {
  var storage = new FakeStorage(); var resolver = new FakeResolver(Protection.Clear, true, changed); var engine = new FakeEngine { ExpireFirst = true };
  var coordinator = new DownloadCoordinator([resolver], new FakeSelector(), new FakePlanner(), [engine], new FakeMedia(), new FakeScanner(ScanVerdict.Clean), storage);
  var result = await coordinator.RunAsync("https://example.invalid/", storage.Root, new Progress<JobProgress>(), default);
  Assert.Equal(expected, result.Stage); Assert.Equal(2, resolver.Calls); Assert.Equal(changed ? 1 : 2, engine.Calls);
 }
 private sealed class FakeResolver(Protection protection, bool refreshable = false, bool changed = false) : IResolver
 {
  public int Calls;
  public string Name => "fixture";
  public Task<IReadOnlyList<PlaybackCandidate>> ResolveAsync(Uri page, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlaybackCandidate>>([new(++Calls > 1 && changed ? "different" : "fixture", "fixture video", page, MediaKind.Direct, [new("v", page, "video")], protection, "fixture", true, Refreshable: refreshable)]);
 }
 private sealed class FakeSelector : ICandidateSelector { public Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates, CancellationToken ct) => Task.FromResult(candidates[0]); }
 private sealed class FakePlanner : IAcquisitionPlanner { public IReadOnlyList<AcquisitionPlan> Plan(PlaybackCandidate candidate) => [new(candidate, "fixture")]; }
 private sealed class FakeEngine : IDownloadEngine
 {
  public string Name => "fixture"; public int Calls; public bool Cancel, ExpireFirst;
  public Task<AcquisitionResult> DownloadAsync(AcquisitionPlan plan, string directory, IProgress<JobProgress> progress, CancellationToken ct)
  { Calls++; if (Cancel) throw new OperationCanceledException(); if (ExpireFirst && Calls == 1) throw new DownloadFailure(FailureCode.AccessRequired, "fixture expired"); return Task.FromResult(new AcquisitionResult(["fixture.mp4"], Name, true)); }
 }
 private sealed class FakeMedia : IMediaPipeline
 {
  public Task<(string Path, MediaInfo Media, Mp4Plan Plan)> NormalizeAsync(AcquisitionResult input, string directory, IProgress<JobProgress> progress, CancellationToken ct) =>
   Task.FromResult(("fixture.mp4", new MediaInfo("fixture.mp4", "mp4", new("h264", "yuv420p", "High", 40, 100, 100, false, 3, "30/1"), null, 100, true), new Mp4Plan(Mp4Mode.PassThrough, false, false)));
 }
 private sealed class FakeScanner(ScanVerdict verdict) : IScanner { public Task<ScanVerdict> ScanAsync(string path, CancellationToken ct) => Task.FromResult(verdict); }
 private sealed class FakeStorage : IJobStorage
 {
  public string Root { get; } = Path.Combine(Path.GetTempPath(), "mp4-coordinator-" + Guid.NewGuid().ToString("N"));
  public int Commits; public bool Cleaned, FailHistory, FailCleanup; public List<HistoryEntry> Entries = [];
  public string CreateJob(Guid id) { Directory.CreateDirectory(Root); return Root; }
  public void EnsureSpace(string jobDirectory, string destination, long requiredBytes) { }
  public Task<string> CommitAsync(string path, string destination, string title, CancellationToken ct) { Commits++; return Task.FromResult(Path.Combine(Root, "fixture.mp4")); }
  public Task RecordAsync(HistoryEntry entry, CancellationToken ct) { if (FailHistory) throw new IOException(); Entries.Add(entry); return Task.CompletedTask; }
  public Task CleanupAsync(string directory) { if (FailCleanup) throw new IOException("cleanup fixture"); if (Directory.Exists(directory)) Directory.Delete(directory, true); Cleaned = true; return Task.CompletedTask; }
 }
}
