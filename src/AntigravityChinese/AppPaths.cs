using System.Reflection;
using System.Text.Json;
using AntigravityChinese.Core;

namespace AntigravityChinese;

internal static class AppPaths
{
    public static TranslationPackage LoadPackage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AntigravityChinese.Pack.json")!;
        return JsonSerializer.Deserialize<TranslationPackage>(stream, JsonFiles.Options) ?? throw new InvalidDataException("内置译文包为空。");
    }

    public static bool CanWrite(Installation installation)
    {
        foreach (var directory in new[] { installation.Root, Path.GetDirectoryName(installation.Asar)! })
        {
            var probe = Path.Combine(directory, ".antigravity-chinese-write-" + Guid.NewGuid().ToString("N"));
            var created = false;
            try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) created = true; }
            catch (UnauthorizedAccessException) { return false; }
            finally { if (created) File.Delete(probe); }
        }
        return true;
    }
}
