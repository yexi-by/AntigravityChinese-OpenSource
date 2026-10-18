using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using AntigravityChinese.Core;
using Microsoft.Win32;

namespace AntigravityChinese;

public partial class MainWindow : Window
{
    private readonly TranslationPackage package = AppPaths.LoadPackage();
    private Installation? installation;
    private bool busy;
    private bool initialized;
    private bool canApply;
    private bool canRestore;

    public MainWindow()
    {
        InitializeComponent();
        ToolVersionText.Text = "v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        PackageText.Text = "简体中文";
        CoverageText.Text = $"内置 {package.Entries.Count:N0} 条译文 · 品牌与专业名称保留原文";
        Closing += (_, e) => { if (busy) { e.Cancel = true; Log("操作正在完成，请稍候。"); } };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await InitializeAsync();
    internal async Task InitializeAsync() { if (initialized) return; initialized = true; await DetectAsync(null); }
    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync(null);
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择 Antigravity App 安装目录", Multiselect = false };
        if (picker.ShowDialog(this) == true) await DetectAsync(picker.FolderName);
    }
    private async Task DetectAsync(string? path)
    {
        SetBusy(true);
        try
        {
            installation = await Task.Run(() => path is null ? AppLocator.Detect() : AppLocator.Inspect(path));
            canApply = false; canRestore = false;
            if (installation is null)
            {
                DetectionText.Text = "未找到安装"; StateText.Text = "请选择安装位置";
                CompatibilityText.Text = "自动检测未找到 Antigravity App，请点击“选择目录”。"; return;
            }
            var selected = installation;
            PathBox.Text = selected.Root; DetectionText.Text = "已检测到 " + selected.Version;
            var state = await Task.Run(() => PatchInstaller.FindApplied(selected));
            var applied = await Task.Run(() => state is not null && state.Value.Receipt.Phase == "applied" &&
                state.Value.Receipt.PackageHash == PatchBuilder.PackageHash(package) &&
                state.Value.Receipt.ServerHash == AsarArchive.FileHash(selected.Server) &&
                (state.Value.Receipt.BundleHash is null || File.Exists(Path.Combine(selected.Root, NativeBundleAdapter.ResourceDirectory, "web.zip")) &&
                    AsarArchive.FileHash(Path.Combine(selected.Root, NativeBundleAdapter.ResourceDirectory, "web.zip")) == state.Value.Receipt.BundleHash));
            canApply = package.Entries.Count > 0 && !applied;
            canRestore = await Task.Run(() => PatchInstaller.CanRestore(selected));
            var partial = applied && state is { } installed && installed.Receipt.Notices.Count > 0;
            StateText.Text = applied ? partial ? "已部分汉化" : "已汉化" : canRestore ? "可以更新汉化" : "可以汉化";
            CompatibilityText.Text = partial ? "已应用可识别部分，其余内容保留原文。补充适配后可更新汉化。" : applied ? "已应用当前内置译文。官方更新后可重新运行本工具。" :
                "按当前资源匹配译文，未匹配内容保留原文。支持对未知版本进行安全匹配。";
            SafetyText.Text = canRestore ? "已保存原始备份，可恢复本次安装。" : "汉化前备份；更新后不会用旧备份覆盖。";
        }
        catch (Exception e)
        {
            installation = null; canApply = false; canRestore = false;
            DetectionText.Text = "检测未完成"; StateText.Text = "请检查安装位置"; CompatibilityText.Text = e.Message; Log(e.Message);
        }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(false);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(true);
    private async Task ExecuteAsync(bool restore)
    {
        if (installation is null) return;
        if (!string.Equals(PathBox.Text.Trim(), installation.Root, StringComparison.OrdinalIgnoreCase))
        {
            await DetectAsync(PathBox.Text);
            if (installation is null || (restore ? !canRestore : !canApply)) return;
        }
        var selected = installation;
        if (AppLocator.IsRunning(selected))
        {
            Log("请先结束 App 中正在进行的任务，再从 Antigravity 托盘菜单退出，然后重试。");
            StageText.Text = "请先退出 Antigravity App"; return;
        }
        SetBusy(true); var work = PatchPaths.NewWork(); var succeeded = false;
        try
        {
            StageText.Text = restore ? "正在恢复原版" : "正在分析并生成中文资源"; WorkBar.IsIndeterminate = true;
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (principal.IsInRole(WindowsBuiltInRole.Administrator) || AppPaths.CanWrite(selected))
            {
                if (restore) await Task.Run(() => PatchInstaller.Restore(selected));
                else
                {
                    var progress = new Progress<WorkProgress>(p => { StageText.Text = p.Stage; Log(p.Message); });
                    var prepared = await Task.Run(() => PatchBuilder.Prepare(selected, package, work, progress: progress));
                    await Task.Run(() => PatchInstaller.Apply(prepared));
                    Log($"已应用 {prepared.Receipt.TranslatedTexts:N0} 处译文；{prepared.Receipt.RetainedTexts:N0} 处保留原文。");
                    foreach (var notice in prepared.Receipt.Notices) Log(notice);
                }
            }
            else
            {
                Log("写入此安装位置需要管理员权限，请确认 Windows 权限提示。");
                var result = Path.Combine(work, "result.json");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { restore ? "--restore" : "--apply", selected.Root, "--result", result }) start.ArgumentList.Add(argument);
                using var helper = Process.Start(start) ?? throw new IOException("无法启动管理员操作。");
                await helper.WaitForExitAsync();
                var outcome = File.Exists(result) ? JsonFiles.Read<HelperResult>(result) : throw new IOException("未收到操作结果，请重新检测。");
                if (!outcome.Success) throw new IOException(outcome.Message); Log(outcome.Message);
            }
            succeeded = true;
            StageText.Text = restore ? "已恢复原版" : "汉化完成"; WorkBar.Value = 100; PercentText.Text = "100%";
            Log(restore ? "原始资源已恢复。" : "资源校验通过，原始备份已保留。");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { StageText.Text = "操作已取消"; Log("管理员权限提示已取消。"); }
        catch (Exception e) { StageText.Text = "操作未完成"; Log(e.Message); }
        finally
        {
            WorkBar.IsIndeterminate = false; SetBusy(false); await DetectAsync(selected.Root);
            try { PatchPaths.CleanupWork(work); } catch (IOException) { Log("临时文件清理未完成，备份仍保留。"); }
        }
        if (succeeded)
        {
            try { Process.Start(new ProcessStartInfo(selected.Executable) { UseShellExecute = true }); Log("已打开 Antigravity App。"); }
            catch (Exception e) { Log("可手动打开 App：" + e.Message); }
        }
    }
    private void SetBusy(bool value)
    {
        busy = value; BrowseButton.IsEnabled = DetectButton.IsEnabled = PathBox.IsEnabled = !value;
        ApplyButton.IsEnabled = !value && canApply; RestoreButton.IsEnabled = !value && canRestore;
    }
    private void Log(string message) { LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}"); LogBox.ScrollToEnd(); }
}
