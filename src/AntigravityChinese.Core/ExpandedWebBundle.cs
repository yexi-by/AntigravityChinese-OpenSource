using System.IO.Compression;
using System.Security.Cryptography;

namespace AntigravityChinese.Core;

// 使用原生目录入口，避免 Windows 外部 ZIP 加载时子目录资源缺失。
public static class ExpandedWebBundle
{
    public static string DirectoryPath(Installation app, string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("前端资源摘要无效。");
        return Path.GetFullPath(Path.Combine(app.Root, NativeBundleAdapter.ResourceDirectory, "web-" + hash));
    }

    public static Dictionary<string, string> ReadHashes(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')) || !paths.Add(name))
                throw new InvalidDataException("前端资源含有无法安全展开的路径：" + entry.FullName);
            if (entry.FullName.EndsWith('/')) continue;
            using var input = entry.Open();
            hashes.Add(name, Convert.ToHexStringLower(SHA256.HashData(input)));
        }
        return hashes;
    }

    public static void Install(Installation app, string zip, string hash)
    {
        var hashes = ReadHashes(zip);
        var target = DirectoryPath(app, hash);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("中文资源目录不能是重解析目录。");
        if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("展开的资源目录不能是重解析目录。");
        var stage = target + ".new-" + Guid.NewGuid().ToString("N");
        var previous = target + ".old-" + Guid.NewGuid().ToString("N");
        var installed = false;
        try
        {
            ZipFile.ExtractToDirectory(zip, stage);
            foreach (var (name, expected) in hashes)
                if (AsarArchive.FileHash(Path.Combine(stage, name)) != expected)
                    throw new IOException("展开后的前端资源校验失败：" + name);
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try { Directory.Move(stage, target); installed = true; }
            catch
            {
                if (Directory.Exists(previous)) Directory.Move(previous, target);
                throw;
            }
        }
        finally
        {
            // 两个路径都由已验证的摘要和本次随机后缀生成，严格限定在本工具资源目录。
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (installed && Directory.Exists(previous)) Directory.Delete(previous, true);
        }
    }

    public static void Remove(Installation app, string hash)
    {
        var directory = DirectoryPath(app, hash);
        var parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("中文资源目录不能是重解析目录。");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("展开的资源目录不能是重解析目录。");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
