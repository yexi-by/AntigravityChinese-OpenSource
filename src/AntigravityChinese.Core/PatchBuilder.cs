using System.Text;
using System.Text.Json;
using Acornima;

namespace AntigravityChinese.Core;

public static class PatchBuilder
{
    public static string PackageHash(TranslationPackage package) => JsonFiles.Hash(
        typeof(PatchBuilder).Assembly.GetName().Version + "\n" + JsonSerializer.Serialize(package, JsonFiles.Options));

    public static PreparedPatch Prepare(Installation installed, TranslationPackage package, string directory,
        string? dataRoot = null, IProgress<WorkProgress>? progress = null)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("请使用空的准备目录。");
        var entries = package.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var entry in entries.Values) ProtectedTerms.Validate(entry.Source, entry.Translation);
        var matcher = new TranslationMatcher(package);
        var expectedAsar = AsarArchive.FileHash(installed.Asar); var expectedExe = AsarArchive.FileHash(installed.Executable);
        var active = PatchInstaller.FindApplied(installed, dataRoot);
        var original = active is null ? installed : installed with
        {
            Asar = Path.Combine(active.Value.Directory, "app.asar.original"),
            Executable = Path.Combine(active.Value.Directory, "Antigravity.exe.original")
        };
        var existingBundle = Path.Combine(installed.Root, NativeBundleAdapter.ResourceDirectory, "web.zip");
        var expectedBundleHash = File.Exists(existingBundle) ? AsarArchive.FileHash(existingBundle) : null;
        var ownsBundle = expectedBundleHash is null || PatchInstaller.OwnsBundle(installed, expectedBundleHash, dataRoot);
        var originalAsarHash = AsarArchive.FileHash(original.Asar); var originalExeHash = AsarArchive.FileHash(original.Executable);
        var backup = active?.Directory ?? Path.Combine(PatchPaths.Backups(installed, dataRoot), JsonFiles.Hash(originalAsarHash + originalExeHash)[..16]);
        var notices = new List<string>(); var translated = 0; var retained = 0;
        using var asar = new AsarArchive(original.Asar);
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        string Patch(string code, string file, bool desktop)
        {
            List<UiGroup> groups;
            try { groups = JavaScriptUi.Extract(code, file, desktop); }
            catch (ParseErrorException) { notices.Add($"脚本语法尚未识别，保留原文：{file}"); return code; }
            var texts = groups.SelectMany(g => g.Texts).ToList();
            var applicable = groups.SelectMany(g => g.Texts.Select(t => (Text: t, Translation: matcher.Find(t, g.Kind))))
                .Where(p => p.Translation is not null).DistinctBy(p => p.Text.Key).ToDictionary(p => p.Text.Key, p => p.Translation!, StringComparer.Ordinal);
            translated += texts.Count(t => applicable.ContainsKey(t.Key)); retained += texts.Count - texts.Count(t => applicable.ContainsKey(t.Key));
            return JavaScriptUi.Apply(code, texts, applicable);
        }

        foreach (var file in asar.PackedFiles.Where(f => f.StartsWith("dist/", StringComparison.Ordinal) && f.EndsWith(".js", StringComparison.Ordinal) &&
                     !f.EndsWith(".test.js", StringComparison.Ordinal) && !f.Contains("/__mocks__/", StringComparison.Ordinal) && !f.Contains("/test/", StringComparison.Ordinal)))
        {
            var code = asar.ReadText(file); var patched = Patch(code, "asar/" + file, true);
            if (patched != code) replacements[file] = Encoding.UTF8.GetBytes(patched);
        }
        string? bundleHash = null;
        var desktopTranslated = translated;
        var serverOriginalHash = AsarArchive.FileHash(installed.Server);
        try
        {
            if (!ownsBundle) throw new InvalidDataException("现有 web.zip 无法确认为本工具生成，保留该文件及原版前端加载方式。");
            if (!NativeBundleAdapter.ServerSupportsOverride(installed.Server)) throw new InvalidDataException("本机服务尚未识别到原生资源包参数。");
            if (!asar.PackedFiles.Contains("dist/languageServer.js", StringComparer.Ordinal)) throw new InvalidDataException("前端加载脚本尚未识别。");
            using var bundle = EmbeddedWebBundle.Open(installed.Server);
            var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in bundle.UiScripts())
            {
                progress?.Report(new("生成中文界面", $"正在处理 {file}"));
                var code = bundle.ReadText(file); var patched = Patch(code, "web/" + file, false);
                if (patched != code) changes[file] = Encoding.UTF8.GetBytes(patched);
            }
            if (changes.Count > 0)
            {
                var loader = replacements.TryGetValue("dist/languageServer.js", out var bytes) ? Encoding.UTF8.GetString(bytes) : asar.ReadText("dist/languageServer.js");
                var adapted = NativeBundleAdapter.Attach(loader) ?? throw new InvalidDataException("启动结构发生变化，尚未匹配到原生资源包入口。");
                bundle.AddCacheKeys(changes);
                bundle.WritePatched(Path.Combine(directory, "web.zip"), changes);
                var hook = NativeBundleAdapter.CreateHook(installed, Path.Combine(directory, "web.zip"), serverOriginalHash);
                bundleHash = AsarArchive.FileHash(Path.Combine(directory, "web.zip"));
                replacements["dist/languageServer.js"] = Encoding.UTF8.GetBytes(adapted.Code);
                replacements[NativeBundleAdapter.HookFile] = hook;
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ParseErrorException)
        {
            // 不把未知版本升级成整体拒绝；未识别的前端部分保留原样。
            notices.Add("前端资源保持原样：" + e.Message);
            // 若前端匹配后加载适配失败，计数按最终实际写入重新计算。
            retained += translated - desktopTranslated;
            translated = desktopTranslated;
        }
        if (AsarArchive.FileHash(installed.Server) != serverOriginalHash) throw new IOException("本机服务在准备过程中发生变化，请重新操作。");
        if (translated == 0) throw new InvalidDataException("本次没有可安全应用的译文，当前安装保持原样。" + string.Join(' ', notices));
        var patchedAsar = Path.Combine(directory, "app.asar"); asar.WritePatched(patchedAsar, replacements);
        using (var verify = new AsarArchive(patchedAsar))
            foreach (var file in asar.PackedFiles.Union(replacements.Keys))
                if (!verify.Read(file).AsSpan().SequenceEqual(replacements.TryGetValue(file, out var data) ? data : asar.Read(file)))
                    throw new InvalidDataException($"桌面资源验证失败：{file}");
        var patchedExe = Path.Combine(directory, "Antigravity.exe");
        using (var patched = new AsarArchive(patchedAsar)) ElectronIntegrity.CreatePatchedExecutable(original.Executable, patchedExe, asar.HeaderHash, patched.HeaderHash);
        File.Copy(original.Asar, Path.Combine(directory, "app.asar.original"));
        File.Copy(original.Executable, Path.Combine(directory, "Antigravity.exe.original"));
        var receipt = new PatchReceipt(installed.Root, installed.Version, "prepared",
            [new("resources/app.asar", originalAsarHash, AsarArchive.FileHash(patchedAsar)),
             new("Antigravity.exe", originalExeHash, AsarArchive.FileHash(patchedExe))],
            bundleHash, translated, retained, notices, PackageHash(package), serverOriginalHash);
        return new(installed, directory, backup, expectedAsar, expectedExe, serverOriginalHash, expectedBundleHash, receipt);
    }
}
