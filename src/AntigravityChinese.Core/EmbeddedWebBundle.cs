using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AntigravityChinese.Core;

// 读取 Go 二进制中的标准 ZIP；不依赖固定地址、文件长度或应用版本号。
public sealed class EmbeddedWebBundle : IDisposable
{
    private readonly MemoryStream stream;
    private readonly ZipArchive archive;
    public byte[] OriginalZip { get; }
    public IEnumerable<string> Files => archive.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/'));

    private EmbeddedWebBundle(byte[] zip)
    {
        OriginalZip = zip;
        stream = new MemoryStream(zip, false);
        archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidDataException("前端资源包存在重复文件。");
    }

    public static EmbeddedWebBundle Open(string executable)
    {
        var bytes = File.ReadAllBytes(executable);
        EmbeddedWebBundle? found = null;
        for (var end = 0; end <= bytes.Length - 22; end++)
        {
            if (bytes[end] != 0x50 || bytes[end + 1] != 0x4b || bytes[end + 2] != 5 || bytes[end + 3] != 6) continue;
            var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12));
            var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
            var start = end - (long)centralSize - centralOffset;
            var finish = end + 22L + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 20));
            if (start < 0 || start > end || finish > bytes.Length || finish - start > int.MaxValue ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)start)) != 0x04034b50) continue;
            EmbeddedWebBundle? candidate = null;
            try
            {
                candidate = new EmbeddedWebBundle(bytes.AsSpan((int)start, (int)(finish - start)).ToArray());
                if (!candidate.Files.Contains("index.html", StringComparer.Ordinal) ||
                    !candidate.Files.Any(f => f.EndsWith(".js", StringComparison.Ordinal)))
                { candidate.Dispose(); continue; }
            }
            catch (InvalidDataException) { candidate?.Dispose(); continue; }
            if (found is not null)
            {
                found.Dispose(); candidate.Dispose();
                throw new InvalidDataException("找到多个前端资源包，无法安全确定界面入口。");
            }
            found = candidate;
        }
        return found ?? throw new InvalidDataException("没有识别到内嵌前端资源包，保留这部分原文。");
    }

    public byte[] Read(string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"资源不存在：{name}");
        using var input = entry.Open(); using var output = new MemoryStream();
        input.CopyTo(output); return output.ToArray();
    }

    public string ReadText(string name) => new UTF8Encoding(false, true).GetString(Read(name));

    public IReadOnlyList<string> EntryScripts()
    {
        var html = ReadText("index.html");
        return System.Text.RegularExpressions.Regex.Matches(html,
                "<script\\b[^>]*\\bsrc=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Split('?', '#')[0].TrimStart('/')).Select(f => f.StartsWith("./", StringComparison.Ordinal) ? f[2..] : f).Where(f => !f.Contains(':') &&
                !f.Contains("..", StringComparison.Ordinal) && Files.Contains(f, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public IEnumerable<string> UiScripts() => Files.Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".mjs", StringComparison.Ordinal))
        .Where(f => JavaScriptUi.MayContainUi(ReadText(f))).Order(StringComparer.Ordinal);

    public void AddCacheKeys(Dictionary<string, byte[]> replacements)
    {
        var html = ReadText("index.html");
        var patched = System.Text.RegularExpressions.Regex.Replace(html, "<script\\b[^>]*>", match =>
        {
            var attribute = System.Text.RegularExpressions.Regex.Match(match.Value, "\\bsrc=([\"'])([^\"']+)\\1");
            if (!attribute.Success) return match.Value;
            var original = attribute.Groups[2].Value;
            var relative = original.Split('?', '#')[0].TrimStart('/');
            if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (!replacements.TryGetValue(relative, out var bytes)) return match.Value;
            if (System.Text.RegularExpressions.Regex.IsMatch(match.Value, "\\bintegrity\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("脚本入口带有尚未适配的完整性声明，保留原版界面。");
            var key = JsonFiles.Hash(Convert.ToBase64String(bytes))[..16];
            var url = original + (original.Contains('?') ? "&" : "?") + "antigravity_chinese=" + key;
            return match.Value[..attribute.Groups[2].Index] + url + match.Value[(attribute.Groups[2].Index + original.Length)..];
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (patched != html) replacements["index.html"] = Encoding.UTF8.GetBytes(patched);
    }

    public void WritePatched(string path, IReadOnlyDictionary<string, byte[]> replacements)
    {
        using (var output = new ZipArchive(File.Open(path, FileMode.CreateNew), ZipArchiveMode.Create))
        {
            foreach (var entry in archive.Entries)
            {
                var target = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                target.LastWriteTime = entry.LastWriteTime;
                using var destination = target.Open();
                if (replacements.TryGetValue(entry.FullName, out var bytes)) destination.Write(bytes);
                else { using var input = entry.Open(); input.CopyTo(destination); }
            }
        }
        using var verify = new EmbeddedWebBundle(File.ReadAllBytes(path));
        foreach (var file in Files)
            if (!verify.Read(file).AsSpan().SequenceEqual(replacements.TryGetValue(file, out var data) ? data : Read(file)))
                throw new InvalidDataException($"前端资源写回验证失败：{file}");
    }

    public void Dispose() { archive.Dispose(); stream.Dispose(); }
}
