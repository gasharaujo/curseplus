using System.IO;
using System.Windows;

namespace CursePlus.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--verify-package")
        {
            try
            {
                var bundle = await new Core.BundleArchive().ReadAsync(e.Args[1]);
                await Core.AppStore.WriteJsonAsync(e.Args[2], new { success = true, bundle.SchemaVersion, changesOnly = bundle.Delta is not null, mods = bundle.Desired.Mods.Count, files = bundle.Desired.Files.Count });
                Shutdown(0);
            }
            catch (Exception error) { await Core.AppStore.WriteJsonAsync(e.Args[2], new { success = false, error = error.Message }); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--native-test")
        {
            try { await Native.NativeValidation.RunAsync(e.Args[1], e.Args[2]); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(e.Args[2], error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--inspect-ui")
        {
            try { Native.CurseForgeAutomation.DumpUi(e.Args[1]); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "CursePlus", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke")
        {
            var directory = Path.GetFullPath(e.Args[1]);
            var window = new MainWindow(Path.Combine(directory, "data")); window.Show();
            try { await Task.Delay(1500); await window.CaptureScreenshotsAsync(directory); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        singleInstance = new Mutex(true, @"Local\CursePlus.Desktop", out var created);
        if (!created) { singleInstance.Dispose(); singleInstance = null; MessageBox.Show("O CursePlus já está aberto.", "CursePlus"); Shutdown(); return; }
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { singleInstance?.ReleaseMutex(); singleInstance?.Dispose(); base.OnExit(e); }
}
