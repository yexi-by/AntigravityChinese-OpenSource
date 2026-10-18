namespace AntigravityChinese.Core;

public static class PatchPaths
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityChinese");
    public static string Backups(Installation app, string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DataRoot, "backups", JsonFiles.Hash(Path.GetFullPath(app.Root).ToUpperInvariant())[..16]);

    public static string NewWork()
    {
        var path = Path.Combine(DataRoot, "work", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }

    public static void CleanupWork(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(DataRoot, "work")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("工作目录超出了工具范围。");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
