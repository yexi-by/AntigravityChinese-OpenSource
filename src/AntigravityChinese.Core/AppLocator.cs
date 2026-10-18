using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AntigravityChinese.Core;

public static class AppLocator
{
    public static Installation Inspect(string selected)
    {
        var path = Path.GetFullPath(selected.Trim().Trim('"'));
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        foreach (var root in new[] { path, Directory.GetParent(path)?.FullName,
                     Directory.GetParent(path)?.Parent?.FullName }.OfType<string>())
        {
            var exe = Path.Combine(root, "Antigravity.exe");
            var asar = Path.Combine(root, "resources", "app.asar");
            var server = Path.Combine(root, "resources", "bin", "language_server.exe");
            if (!File.Exists(exe) || !File.Exists(asar) || !File.Exists(server)) continue;
            using var archive = new AsarArchive(asar);
            using var package = JsonDocument.Parse(archive.Read("package.json"));
            var json = package.RootElement;
            if (json.GetProperty("name").GetString() != "antigravity" ||
                !json.GetProperty("main").GetString()!.StartsWith("dist/", StringComparison.Ordinal)) continue;
            return new(root, exe, asar, server, json.GetProperty("version").GetString() ?? "未知版本");
        }
        throw new DirectoryNotFoundException("没有找到 Antigravity App，请选择它的安装目录。");
    }

    public static Installation? Detect()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity")
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display &&
                    display.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) &&
                    entry.GetValue("InstallLocation") is string location) candidates.Insert(0, location);
            }
        }
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { return Inspect(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    public static bool IsRunning(Installation installation)
    {
        foreach (var process in Process.GetProcessesByName("Antigravity"))
        {
            using (process)
            {
                var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
                if (handle == IntPtr.Zero)
                {
                    if (Marshal.GetLastWin32Error() == 87) continue; // 枚举后进程已经退出。
                    throw new IOException("无法确认 Antigravity 进程的安装位置，请退出 App 后再操作。");
                }
                try
                {
                    // MainModule 需要读取沙盒子进程内存，可能把其他安装误报为目标正在运行。
                    var path = new StringBuilder(32768); var length = (uint)path.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, path, ref length))
                        throw new IOException("无法读取 Antigravity 进程位置，请退出 App 后再操作。");
                    if (string.Equals(path.ToString(), installation.Executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                finally { CloseHandle(handle); }
            }
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
