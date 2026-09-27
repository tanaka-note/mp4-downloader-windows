using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mp4Downloader.Core;
using Mp4Downloader.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Mp4Downloader.App;

public sealed partial class MainWindow : Window, ICandidateSelector
{
 private readonly JobStorage storage = new(App.TestRoot is null ? null : Path.Combine(App.TestRoot, "AppData"));
 private readonly AuthVault vault = new();
 private readonly PublicNetworkProxy networkProxy = new();
 private readonly HttpTransport http;
 private readonly DownloadCoordinator coordinator;
 private readonly string browserDirectory;
 private CancellationTokenSource? cancellation;
 private string? saved;
 private bool busy;
 private bool closeAfterJob;
 public MainWindow()
 {
  InitializeComponent();
  Title = "MP4 Downloader v" + typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
  AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 680));
  browserDirectory = Path.Combine(storage.Root, "WebView2");
  http = new(vault);
  var tools = new ToolCatalog(Path.Combine(AppContext.BaseDirectory, "tools"));
  var processes = new ProcessRunner();
  var manifests = new ManifestResolver(http);
  coordinator = new([new DirectResolver(http), manifests, new YtDlpResolver(tools, processes, manifests, vault, () => storage.ActiveDirectory), new HtmlResolver(http, manifests),
    new BrowserResolver(this, http, manifests, vault, browserDirectory, networkProxy.Address)], this, new AcquisitionPlanner(),
   [new DirectDownloadEngine(http), new YtDlpDownloadEngine(tools, processes), new ManifestDownloadEngine(tools, processes), new HlsDownloadEngine(http)],
   new MediaPipeline(tools, processes, new MediaProbe(tools, processes)), new DefenderScanner(processes), storage);
  DestinationBox.Text = LoadDestination();
  Root.Loaded += async (_, _) => { await storage.RecoverAsync(); await RefreshHistoryAsync(); };
  AppWindow.Closing += (_, e) => { if (busy) { e.Cancel = true; closeAfterJob = true; cancellation?.Cancel(); Status.Text = "処理を中止して終了しています"; } };
  Closed += (_, _) => { networkProxy.Dispose(); http.Dispose(); storage.Dispose(); vault.Clear(); };
 }
 private async void Download_Click(object sender, RoutedEventArgs e)
 {
  if (busy) return;
  using var identity = WindowsIdentity.GetCurrent();
  if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { Status.Text = "管理者として起動せず、通常のユーザー権限で起動してください。"; return; }
  SetBusy(true);
  saved = null;
  cancellation = new();
  var progress = new Progress<JobProgress>(p => { Status.Text = p.Message; Progress.IsIndeterminate = p.Fraction is null && busy; if (p.Fraction is { } value) Progress.Value = value * 100; });
  try
  {
   var result = await coordinator.RunAsync(UrlBox.Text, DestinationBox.Text, progress, cancellation.Token);
   saved = result.SavedPath;
   Status.Text = result.Message;
  }
  catch (DownloadFailure ex) { Status.Text = ex.Message; }
  finally
  {
   cancellation.Dispose(); cancellation = null; vault.Clear(); SetBusy(false); Progress.IsIndeterminate = false;
   await RefreshHistoryAsync();
   if (closeAfterJob) Close();
  }
 }
 private void SetBusy(bool value)
 {
  busy = value;
  DownloadButton.IsEnabled = PasteButton.IsEnabled = FolderButton.IsEnabled = UrlBox.IsEnabled = ClearHistoryButton.IsEnabled = ClearTempButton.IsEnabled = ClearBrowserButton.IsEnabled = !value;
  CancelButton.IsEnabled = value;
  OpenButton.IsEnabled = !value && saved is not null;
 }
 private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
 private async void Paste_Click(object sender, RoutedEventArgs e)
 {
  try { var content = Clipboard.GetContent(); if (content.Contains(StandardDataFormats.Text)) UrlBox.Text = await content.GetTextAsync(); }
  catch (Exception) { Status.Text = "クリップボードを読み取れませんでした。URLを直接貼り付けてください。"; }
 }
 private async void Folder_Click(object sender, RoutedEventArgs e)
 {
  var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
  WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
  var folder = await picker.PickSingleFolderAsync();
  if (folder is not null)
  {
   DestinationBox.Text = folder.Path;
   try { await File.WriteAllTextAsync(Path.Combine(storage.Root, "settings.json"), JsonSerializer.Serialize(new { Destination = folder.Path })); }
   catch (IOException) { Status.Text = "保存先の設定を記録できませんでした。"; }
  }
 }
 private static void ShowFolder(string path)
 {
  if (!File.Exists(path)) return;
  var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
  start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start);
 }
 private void Open_Click(object sender, RoutedEventArgs e) { if (saved is not null) ShowFolder(saved); }
 private async void ClearHistory_Click(object sender, RoutedEventArgs e) { storage.ClearHistory(); await RefreshHistoryAsync(); }
 private async void ClearTemp_Click(object sender, RoutedEventArgs e) { await storage.RecoverAsync(); Status.Text = "使用中でない一時ファイルを回収しました。"; }
 private async void ClearBrowser_Click(object sender, RoutedEventArgs e)
 {
  try
  {
   var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, browserDirectory, null);
   var window = new BrowserWindow();
   await window.ClearAsync(environment);
   Status.Text = "WebView2のCookie・閲覧データを削除しました。";
  }
  catch (Exception) { Status.Text = "WebView2データを削除できませんでした。Runtimeまたは使用中のブラウザを確認してください。"; }
 }
 private async Task RefreshHistoryAsync()
 {
  HistoryPanel.Children.Clear();
  foreach (var entry in await storage.ReadHistoryAsync())
  {
   var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 8) };
   row.Children.Add(new TextBlock { Text = $"{entry.Status} · {entry.Title}", TextWrapping = TextWrapping.Wrap });
   row.Children.Add(new TextBlock { Text = $"{entry.Host} · {entry.FileName} · {entry.Size / 1024 / 1024} MB · {entry.CompletedAt:yyyy/MM/dd HH:mm}" });
   if (entry.ErrorCode is { } code) row.Children.Add(new TextBlock { Text = "エラー分類: " + code, TextWrapping = TextWrapping.Wrap });
   if (entry.SavedPath is { } path) { var button = new Button { Content = "保存場所を開く" }; button.Click += (_, _) => ShowFolder(path); row.Children.Add(button); }
   HistoryPanel.Children.Add(row);
  }
 }
 public async Task<PlaybackCandidate> SelectAsync(IReadOnlyList<PlaybackCandidate> candidates, CancellationToken ct)
 {
  var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
  foreach (var candidate in candidates) list.Items.Add($"{candidate.Title} · {candidate.Kind} · {candidate.Width}×{candidate.Height} · {candidate.Duration:0}秒 · {candidate.Tracks[0].Url.Host}");
  var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "保存する動画を選択してください", Content = list, PrimaryButtonText = "この動画を取得", CloseButtonText = "中止", IsPrimaryButtonEnabled = false };
  list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
  using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
  var result = await dialog.ShowAsync();
  ct.ThrowIfCancellationRequested();
  if (result != ContentDialogResult.Primary || list.SelectedIndex < 0) throw new OperationCanceledException();
  return candidates[list.SelectedIndex];
 }
 private string LoadDestination()
 {
  try
  {
   var settings = Path.Combine(storage.Root, "settings.json");
   if (File.Exists(settings)) { using var json = JsonDocument.Parse(File.ReadAllText(settings)); var value = json.RootElement.GetProperty("Destination").GetString(); if (!string.IsNullOrWhiteSpace(value)) return value; }
  }
  catch (Exception) { }
  var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
  if (SHGetKnownFolderPath(downloads, 0, 0, out var pointer) == 0) { try { return Marshal.PtrToStringUni(pointer)!; } finally { Marshal.FreeCoTaskMem(pointer); } }
  return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
 }
 [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folder, uint flags, nint token, out nint path);
}
