using Acornima;

namespace AntigravityChinese.Core;

public static class SourceScanner
{
    public static ScanResult Scan(Installation installation, IProgress<WorkProgress>? progress = null, string? dataRoot = null)
    {
        if (PatchInstaller.FindApplied(installation, dataRoot) is { } applied)
            installation = installation with { Asar = Path.Combine(applied.Directory, "app.asar.original"), Executable = Path.Combine(applied.Directory, "Antigravity.exe.original") };
        var groups = new List<UiGroup>(); var notices = new List<string>();
        using var archive = new AsarArchive(installation.Asar);
        foreach (var file in archive.PackedFiles.Where(f => f.StartsWith("dist/", StringComparison.Ordinal) &&
                     f.EndsWith(".js", StringComparison.Ordinal) && !f.EndsWith(".test.js", StringComparison.Ordinal) &&
                     !f.Contains("/__mocks__/", StringComparison.Ordinal) && !f.Contains("/test/", StringComparison.Ordinal)))
        {
            try { groups.AddRange(JavaScriptUi.Extract(archive.ReadText(file), "asar/" + file, true)); }
            catch (ParseErrorException) { notices.Add($"桌面脚本语法尚未识别，保留原文：{file}"); }
        }
        var signature = "unavailable";
        try
        {
            var serverScript = "dist/languageServer.js";
            if (archive.PackedFiles.Contains(serverScript, StringComparer.Ordinal) && NativeBundleAdapter.ServerSupportsOverride(installation.Server))
                signature = NativeBundleAdapter.Attach(archive.ReadText(serverScript))?.Signature ?? "startup-structure-changed";
            if (signature is "unavailable" or "startup-structure-changed")
                notices.Add("没有识别到原生前端加载入口，当前只应用可安全匹配的桌面文案。");
            using var bundle = EmbeddedWebBundle.Open(installation.Server);
            var webCount = groups.Count;
            foreach (var file in bundle.UiScripts())
            {
                progress?.Report(new("分析界面文案", $"正在分析 {file}"));
                try { groups.AddRange(JavaScriptUi.Extract(bundle.ReadText(file), "web/" + file)); }
                catch (ParseErrorException) { notices.Add($"界面脚本语法尚未识别，保留原文：{file}"); }
            }
            if (groups.Count == webCount) notices.Add("前端资源中尚未识别到界面显示调用，请检查新的打包或渲染方式。");
        }
        catch (Exception e) when (e is InvalidDataException or ParseErrorException or NotSupportedException)
        { notices.Add("前端资源保持原样：" + e.Message); }
        return new(installation.Version, groups, notices, signature);
    }
}
