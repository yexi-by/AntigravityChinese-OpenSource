namespace AntigravityChinese.Core;

public sealed class TranslationMatcher
{
    private readonly Dictionary<string, TranslationEntry> exact;
    private readonly Dictionary<(string Source, string Kind, string Role), string> reusable;

    public TranslationMatcher(TranslationPackage package)
    {
        exact = package.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        reusable = package.Entries.Where(e => e.Kind.Length > 0 && e.Role.Length > 0 && e.Shape.Length > 0)
            .GroupBy(e => (e.Source, e.Kind, e.Role))
            .Where(g => g.Select(e => e.Translation).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Translation);
    }

    public string? Find(UiText text, string kind) => Find(text.Key, text.Source, kind, text.Role);
    public string? Find(CatalogUnit text, string kind) => Find(text.Key, text.Source, kind, text.Role);

    private string? Find(string key, string source, string kind, string role)
    {
        if (exact.TryGetValue(key, out var entry) && entry.Source == source) return entry.Translation;
        // 字面量、缓存值和可选 label 的写入方式可以变化，语义仍由原文、显示种类和角色决定。
        return reusable.GetValueOrDefault((source, kind, role));
    }
}
