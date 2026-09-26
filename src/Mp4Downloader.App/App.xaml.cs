using Microsoft.UI.Xaml;
namespace Mp4Downloader.App;
public partial class App : Application
{
 public static string? TestRoot { get; private set; }
 public static bool ReleaseSmoke { get; private set; }
 private Window? window;
 public App()
 {
  var arguments = Environment.GetCommandLineArgs();
  var test = Array.IndexOf(arguments, "--self-test");
  ReleaseSmoke = arguments.Contains("--release-smoke");
  if (test >= 0 && test + 1 < arguments.Length) TestRoot = Path.GetFullPath(arguments[test + 1]);
  UnhandledException += (_, e) =>
  {
   if (TestRoot is not null) { Directory.CreateDirectory(TestRoot); File.WriteAllText(Path.Combine(TestRoot, "startup-error.txt"), e.Exception.ToString()); }
  };
  InitializeComponent();
 }
 protected override void OnLaunched(LaunchActivatedEventArgs args)
 {
  var arguments = Environment.GetCommandLineArgs();
  var test = Array.IndexOf(arguments, "--self-test");
  if (test >= 0 && test + 1 < arguments.Length) TestRoot = Path.GetFullPath(arguments[test + 1]);
  window = new MainWindow(); window.Activate();
  if (TestRoot is not null) _ = SelfTest.RunAsync(window, TestRoot, ReleaseSmoke);
 }
}
