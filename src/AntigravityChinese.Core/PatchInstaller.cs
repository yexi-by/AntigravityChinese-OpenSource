namespace AntigravityChinese.Core;

public static class PatchInstaller
{
    private static bool Matches(string path, string hash) => File.Exists(path) && AsarArchive.FileHash(path) == hash;
    // 不同原版可能产生相同补丁字节；优先使用最近一次写入的匹配记录，才能恢复到本次原版。
    private static IEnumerable<string> StatePaths(string root) => Directory.EnumerateFiles(root, "state.json", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc);
    private static PatchReceipt ReadState(string path, Installation app)
    {
        var state = JsonFiles.Read<PatchReceipt>(path);
        if (!string.Equals(Path.GetFullPath(state.Root), Path.GetFullPath(app.Root), StringComparison.OrdinalIgnoreCase) ||
            state.Files.Count != 2 || !state.Files.Select(f => f.RelativePath).Order().SequenceEqual(new[] { "Antigravity.exe", "resources/app.asar" }.Order()))
            throw new InvalidDataException("备份记录的安装位置或文件范围无效。");
        return state;
    }

    public static bool CanRestore(Installation app, string? dataRoot = null)
    {
        var root = PatchPaths.Backups(app, dataRoot);
        if (!Directory.Exists(root)) return false;
        return StatePaths(root).Select(p => ReadState(p, app))
            .Any(s => s.Phase != "restored" && s.Files.All(f => Matches(Path.Combine(app.Root, f.RelativePath), f.OriginalHash) ||
                                                                            Matches(Path.Combine(app.Root, f.RelativePath), f.PatchedHash)));
    }
    public static (string Directory, PatchReceipt Receipt)? FindApplied(Installation app, string? dataRoot = null)
    {
        var root = PatchPaths.Backups(app, dataRoot);
        if (!Directory.Exists(root)) return null;
        foreach (var path in StatePaths(root))
        {
            var state = ReadState(path, app);
            if (state.Phase is not ("applied" or "applying") || !string.Equals(state.Root, app.Root, StringComparison.OrdinalIgnoreCase)) continue;
            if (state.Files.All(f => Matches(Path.Combine(app.Root, f.RelativePath), f.PatchedHash)))
                return (Path.GetDirectoryName(path)!, state);
        }
        return null;
    }

    public static bool OwnsBundle(Installation app, string hash, string? dataRoot = null)
    {
        var root = PatchPaths.Backups(app, dataRoot);
        return Directory.Exists(root) && StatePaths(root)
            .Any(path => ReadState(path, app).BundleHash == hash);
    }

    public static void Apply(PreparedPatch prepared)
    {
        using var gate = new Mutex(false, @"Local\AntigravityChinese.Patching");
        if (!gate.WaitOne(0)) throw new IOException("另一项汉化或恢复操作正在进行。");
        try
        {
            var app = prepared.Installation;
            if (AppLocator.IsRunning(app)) throw new IOException("请先从 Antigravity 托盘菜单退出 App，结束进行中的任务后再汉化。");
            if (!Matches(app.Asar, prepared.ExpectedAsarHash) || !Matches(app.Executable, prepared.ExpectedExeHash) ||
                !Matches(app.Server, prepared.ExpectedServerHash))
                throw new IOException("安装文件在准备后发生变化，请重新检测。");
            var bundlePath = Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip");
            if (prepared.ExpectedBundleHash is { } expectedBundle ? !Matches(bundlePath, expectedBundle) : File.Exists(bundlePath))
                throw new IOException("前端资源包在准备后发生变化，请重新检测。");
            foreach (var file in prepared.Receipt.Files)
            {
                var name = Path.GetFileName(file.RelativePath);
                if (!Matches(Path.Combine(prepared.Directory, name), file.PatchedHash) ||
                    !Matches(Path.Combine(prepared.Directory, name + ".original"), file.OriginalHash))
                    throw new IOException("准备文件或原始文件校验失败。");
            }
            if (prepared.Receipt.BundleHash is { } bundle && !Matches(Path.Combine(prepared.Directory, "web.zip"), bundle))
                throw new IOException("中文资源包校验失败。");
            Directory.CreateDirectory(prepared.BackupDirectory);
            foreach (var file in prepared.Receipt.Files)
            {
                var name = Path.GetFileName(file.RelativePath) + ".original";
                var target = Path.Combine(prepared.BackupDirectory, name);
                if (!File.Exists(target)) File.Copy(Path.Combine(prepared.Directory, name), target);
                if (!Matches(target, file.OriginalHash)) throw new IOException("原始备份不匹配，已停止写入。");
            }
            var priorPath = Path.Combine(prepared.BackupDirectory, "state.json");
            var prior = File.Exists(priorPath) ? ReadState(priorPath, app) : null;
            JsonFiles.Write(priorPath, prepared.Receipt with { Phase = "applying" });
            try
            {
                if (prepared.Receipt.BundleHash is not null)
                {
                    ExpandedWebBundle.Install(app, Path.Combine(prepared.Directory, "web.zip"), prepared.Receipt.BundleHash);
                    Replace(Path.Combine(prepared.Directory, "web.zip"), Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip"));
                }
                else if (prior?.BundleHash is { } priorHash && Matches(Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip"), priorHash))
                    File.Delete(Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip"));
                Replace(Path.Combine(prepared.Directory, "app.asar"), app.Asar);
                Replace(Path.Combine(prepared.Directory, "Antigravity.exe"), app.Executable);
                foreach (var file in prepared.Receipt.Files)
                    if (!Matches(Path.Combine(app.Root, file.RelativePath), file.PatchedHash)) throw new IOException("安装后校验失败。");
                if (prepared.Receipt.BundleHash is { } installedBundle && !Matches(Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip"), installedBundle))
                    throw new IOException("安装后的前端资源包校验失败。");
                using var archive = new AsarArchive(app.Asar);
                if (ElectronIntegrity.ReadHeaderHash(app.Executable) is { } integrity && integrity != archive.HeaderHash)
                    throw new IOException("安装后的 Electron 完整性校验不匹配。");
                if (prepared.ExpectedBundleHash is { } oldBundle && oldBundle != prepared.Receipt.BundleHash &&
                    (prepared.Receipt.BundleHash is not null || prior?.BundleHash == oldBundle))
                    ExpandedWebBundle.Remove(app, oldBundle);
                JsonFiles.Write(Path.Combine(prepared.BackupDirectory, "state.json"), prepared.Receipt with { Phase = "applied" });
            }
            catch (Exception error)
            {
                try { RestoreFiles(app, prepared.BackupDirectory, prepared.Receipt); }
                catch (Exception recovery) { throw new IOException($"汉化未完成，恢复也未完成。请保留备份 {prepared.BackupDirectory}。{error.Message}；恢复：{recovery.Message}", recovery); }
                JsonFiles.Write(Path.Combine(prepared.BackupDirectory, "state.json"), prepared.Receipt with { Phase = "restored" });
                throw new IOException("汉化未完成，原始资源已恢复。" + error.Message, error);
            }
        }
        finally { gate.ReleaseMutex(); }
    }

    public static void Restore(Installation app, string? dataRoot = null)
    {
        using var gate = new Mutex(false, @"Local\AntigravityChinese.Patching");
        if (!gate.WaitOne(0)) throw new IOException("另一项汉化或恢复操作正在进行。");
        try
        {
            if (AppLocator.IsRunning(app)) throw new IOException("请先从 Antigravity 托盘菜单退出 App 后再恢复。");
            var root = PatchPaths.Backups(app, dataRoot);
            if (Directory.Exists(root))
                foreach (var path in StatePaths(root))
                {
                    var state = ReadState(path, app);
                    if (state.Phase == "restored") continue;
                    if (!state.Files.All(f => Matches(Path.Combine(app.Root, f.RelativePath), f.PatchedHash) ||
                                              Matches(Path.Combine(app.Root, f.RelativePath), f.OriginalHash))) continue;
                    RestoreFiles(app, Path.GetDirectoryName(path)!, state);
                    JsonFiles.Write(path, state with { Phase = "restored" }); return;
                }
            throw new IOException("当前文件与已有备份不匹配，已停止恢复，保留更新后的安装。");
        }
        finally { gate.ReleaseMutex(); }
    }

    private static void RestoreFiles(Installation app, string backup, PatchReceipt state)
    {
        if (!string.Equals(app.Root, state.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份安装位置不匹配。");
        foreach (var file in state.Files)
        {
            var source = Path.Combine(backup, Path.GetFileName(file.RelativePath) + ".original");
            var target = Path.Combine(app.Root, file.RelativePath);
            if (!Matches(source, file.OriginalHash) || !Matches(target, file.OriginalHash) && !Matches(target, file.PatchedHash))
                throw new IOException("当前安装或备份已发生变化，停止覆盖。");
        }
        foreach (var file in state.Files)
        {
            var target = Path.Combine(app.Root, file.RelativePath);
            if (!Matches(target, file.OriginalHash)) Replace(Path.Combine(backup, Path.GetFileName(file.RelativePath) + ".original"), target);
            if (!Matches(target, file.OriginalHash)) throw new IOException("恢复后校验失败。");
        }
        var bundle = Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web.zip");
        if (state.BundleHash is { } expandedHash) ExpandedWebBundle.Remove(app, expandedHash);
        if (state.BundleHash is { } hash && Matches(bundle, hash)) File.Delete(bundle);
        var folder = Path.GetDirectoryName(bundle)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static void Replace(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = target + ".antigravity-chinese.new";
        try
        {
            File.Copy(source, stage, true);
            using (var file = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite)) file.Flush(true);
            File.Move(stage, target, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
