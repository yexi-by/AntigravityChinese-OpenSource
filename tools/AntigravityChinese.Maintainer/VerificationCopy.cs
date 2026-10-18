using System.Text;
using AntigravityChinese.Core;

// 仅用于维护者验证。生产补丁不包含这些账户隔离和更新检查改动。
internal static class VerificationCopy
{
    public static void Create(Installation source, string destination)
    {
        var original = Path.GetFullPath(source.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(original, StringComparison.OrdinalIgnoreCase) || original.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("验证目录必须与原版安装目录分离。");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("请使用空的验证目录。");
        if (PatchInstaller.FindApplied(source) is not null) throw new IOException("请使用未汉化的原版安装创建验证副本。");
        var app = Path.Combine(destination, "app"); var profile = Path.Combine(destination, "profile");
        Directory.CreateDirectory(app); Directory.CreateDirectory(profile);
        CopyTree(source.Root, app);
        var asarPath = Path.Combine(app, "resources", "app.asar");
        var stagedAsar = Path.Combine(destination, "verification.asar");
        string oldHash, newHash;
        using (var asar = new AsarArchive(asarPath))
        {
            var main = asar.ReadText("dist/main.js");
            const string firstStatement = "\"use strict\";";
            const string updaterStatement = "(0, updater_1.initAutoUpdater)(HEADLESS, settingsService);";
            if (!main.StartsWith(firstStatement, StringComparison.Ordinal) || main.Split(updaterStatement).Length != 2)
                throw new InvalidDataException("验证副本的账户隔离入口发生变化，请先检查当前桌面入口。");
            var profileLiteral = JavaScriptUi.Quote(profile);
            var isolation = $$"""
            // 维护验证专用，使用独立文件目录；系统级登录服务仍由原程序管理。
            for (const name of ['USERPROFILE', 'HOME', 'APPDATA', 'LOCALAPPDATA']) process.env[name] = {{profileLiteral}};
            const verificationApp = require('electron').app;
            for (const name of ['home', 'appData', 'userData', 'sessionData', 'logs']) verificationApp.setPath(name, {{profileLiteral}});
            if (require('node:os').homedir() !== {{profileLiteral}}) throw new Error('验证账户目录隔离未生效，已停止启动。');
            process.env.ANTIGRAVITY_VSCODE_HOST = '1';
            """;
            main = main.Insert(firstStatement.Length, "\n" + isolation + "\n")
                .Replace(updaterStatement, "/* 验证副本不执行官方更新检查。 */", StringComparison.Ordinal);
            JavaScriptUi.Validate(main);
            asar.WritePatched(stagedAsar, new Dictionary<string, byte[]> { ["dist/main.js"] = Encoding.UTF8.GetBytes(main) });
            using var patched = new AsarArchive(stagedAsar);
            oldHash = asar.HeaderHash; newHash = patched.HeaderHash;
        }
        var exe = Path.Combine(app, "Antigravity.exe"); var stagedExe = Path.Combine(destination, "verification.exe");
        ElectronIntegrity.CreatePatchedExecutable(exe, stagedExe, oldHash, newHash);
        File.Copy(stagedAsar, asarPath, true); File.Copy(stagedExe, exe, true);
        File.Delete(stagedAsar); File.Delete(stagedExe);
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("验证复制不跟随重解析目录：" + entry.Name);
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo directory) { Directory.CreateDirectory(destination); CopyTree(directory.FullName, destination); }
            else File.Copy(entry.FullName, destination);
        }
    }
}
