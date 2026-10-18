using System.Text;
using System.Text.Json;

namespace AntigravityChinese.Core;

public static class AttProject
{
    public static int WriteSeedManual(string translationRoot, TranslationPackage package, string path)
    {
        var catalog = JsonFiles.Read<TranslationCatalog>(Path.Combine(translationRoot, "catalog.json"));
        var matcher = new TranslationMatcher(package);
        var retained = (package.RetainedSources ?? []).ToHashSet(StringComparer.Ordinal);
        var output = new StringBuilder(); var count = 0;
        foreach (var scope in catalog.Groups.GroupBy(g => g.Kind.StartsWith("ui/", StringComparison.Ordinal) ? "app-ui" : "desktop"))
        {
            var line = 0;
            foreach (var group in scope)
            {
                line++;
                for (var i = 0; i < group.Units.Count; i++)
                {
                    var unit = group.Units[i];
                    var text = matcher.Find(unit, group.Kind) ?? (retained.Contains(unit.Source) || !ProtectedTerms.NeedsTranslation(unit.Source) ? unit.Source : null);
                    if (text is null) continue;
                    var id = $"{scope.Key}.jsonl:line{line}:unit{i + 1}:text";
                    output.AppendLine("[[translation]]").Append("id = ").AppendLine(QuoteToml(id)).AppendLine("type = 'free'")
                        .Append("source = [").AppendJoin(',', unit.Source.Split('\n').Select(QuoteToml)).AppendLine("]")
                        .Append("translation = [").AppendJoin(',', text.Split('\n').Select(QuoteToml)).AppendLine("]");
                    count++;
                }
            }
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false)); return count;
    }

    // TOML Unicode 转义不接受 JSON 的 UTF-16 代理项；保留实际 Unicode 字符（含 emoji）。
    private static string QuoteToml(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in text)
            if (c is '"' or '\\') quoted.Append('\\').Append(c);
            else if (char.IsControl(c)) quoted.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else quoted.Append(c);
        return quoted.Append('"').ToString();
    }

    public static int UpdateInput(ScanResult scan, string translationRoot)
    {
        Directory.CreateDirectory(translationRoot);
        var path = Path.Combine(translationRoot, "catalog.json");
        var catalog = File.Exists(path) ? JsonFiles.Read<TranslationCatalog>(path) : new([]);
        var known = catalog.Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var group in scan.Groups.DistinctBy(g => g.Key))
        {
            if (!known.Add(group.Key)) continue;
            var units = group.Texts.DistinctBy(t => t.Key).Select((t, i) => new CatalogUnit($"text-{i + 1}", t.Key, t.Source, t.Role, t.Shape)).ToList();
            catalog.Groups.Add(new($"界面-{catalog.Groups.Count + 1:D5}", group.Key, group.Kind, units)); added++;
        }
        // 已见过的语境作为同一套当前翻译资产保存，避免删除旧版文案后丢失其有效译文。
        if (added > 0 || !File.Exists(path))
        {
            JsonFiles.Write(path, catalog);
            var input = Path.Combine(translationRoot, "input"); Directory.CreateDirectory(input);
            foreach (var scope in catalog.Groups.GroupBy(g => g.Kind.StartsWith("ui/", StringComparison.Ordinal) ? "app-ui" : "desktop"))
            {
                var lines = scope.Select(g => JsonSerializer.Serialize(new { id = g.Id, kind = g.Kind,
                    units = g.Units.Select(u => new { id = u.Id, text = u.Source }) }));
                File.WriteAllText(Path.Combine(input, scope.Key + ".jsonl"), string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            }
        }
        return added;
    }

    public static TranslationPackage Import(string translationRoot, string writeBack)
    {
        var catalog = JsonFiles.Read<TranslationCatalog>(Path.Combine(translationRoot, "catalog.json"));
        var translated = new Dictionary<(string Group, string Unit), string>();
        foreach (var file in Directory.EnumerateFiles(writeBack, "*.jsonl", SearchOption.AllDirectories))
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line); var json = document.RootElement;
                var id = json.GetProperty("id").GetString()!;
                foreach (var unit in json.GetProperty("units").EnumerateArray())
                    if (!translated.TryAdd((id, unit.GetProperty("id").GetString()!), unit.GetProperty("text").GetString()!))
                        throw new InvalidDataException("ATT 写回包含重复条目。");
            }
        var entries = new List<TranslationEntry>();
        foreach (var group in catalog.Groups)
            foreach (var unit in group.Units)
            {
                if (!translated.TryGetValue((group.Id, unit.Id), out var text))
                    throw new InvalidDataException($"ATT 写回缺少条目：{group.Id}/{unit.Id}");
                if (text == unit.Source) continue;
                // React 片段前后的空白属于显示拼接契约，保持来源边界。
                var prefix = new string(unit.Source.TakeWhile(char.IsWhiteSpace).ToArray());
                var suffix = new string(unit.Source.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
                text = prefix + text.Trim() + suffix;
                ProtectedTerms.Validate(unit.Source, text);
                if (!JavaScriptUi.Slots().Matches(unit.Source).Select(m => m.Value).SequenceEqual(
                        JavaScriptUi.Slots().Matches(text).Select(m => m.Value)))
                    throw new InvalidDataException($"动态占位符发生变化：{group.Id}/{unit.Id}");
                entries.Add(new(unit.Key, unit.Source, text, group.Kind, unit.Role, unit.Shape));
            }
        if (translated.Count != catalog.Groups.Sum(g => g.Units.Count)) throw new InvalidDataException("ATT 写回含有未登记条目。");
        var keep = Path.Combine(translationRoot, "keep-original.json");
        return new(entries, File.Exists(keep) ? JsonFiles.Read<List<string>>(keep) : []);
    }
}
