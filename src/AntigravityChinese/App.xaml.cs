using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntigravityChinese.Core;

namespace AntigravityChinese;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() is "--apply" or "--restore")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = e.Args.Length == 4 && e.Args[2] == "--result" ? Path.GetFullPath(e.Args[3]) : null;
            string? work = null;
            try
            {
                var scope = Path.GetFullPath(Path.Combine(PatchPaths.DataRoot, "work")) + Path.DirectorySeparatorChar;
                if (result is null || !result.StartsWith(scope, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("操作结果路径无效。");
                var installation = AppLocator.Inspect(e.Args[1]);
                if (e.Args[0] == "--restore") PatchInstaller.Restore(installation);
                else
                {
                    work = PatchPaths.NewWork();
                    var prepared = PatchBuilder.Prepare(installation, AppPaths.LoadPackage(), work); PatchInstaller.Apply(prepared);
                }
                JsonFiles.Write(result, new HelperResult(true, e.Args[0] == "--restore" ? "原始资源已恢复。" : "汉化完成，资源校验通过。")); Shutdown(0);
            }
            catch (Exception error) { if (result is not null && result.StartsWith(Path.GetFullPath(Path.Combine(PatchPaths.DataRoot, "work")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) JsonFiles.Write(result, new HelperResult(false, error.Message)); Shutdown(1); }
            finally { if (work is not null) PatchPaths.CleanupWork(work); }
            return;
        }
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.FirstOrDefault() == "--preview" && e.Args.Length == 2)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; await window.InitializeAsync();
            var content = (FrameworkElement)window.Content; content.Measure(new Size(980, 760)); content.Arrange(new Rect(0, 0, 980, 760)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(980, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(e.Args[1]); encoder.Save(output); Shutdown(0);
        }
        else window.Show();
    }
}
