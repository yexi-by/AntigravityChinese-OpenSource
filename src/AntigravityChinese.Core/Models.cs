using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AntigravityChinese.Core;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"文件为空：{path}");

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stage = path + ".tmp";
        File.WriteAllText(stage, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(stage, path, true);
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record Installation(string Root, string Executable, string Asar, string Server, string Version);
public sealed record ExpressionSlot(int Start, int End);
public sealed record UiText(string Key, string GroupKey, string File, int Start, int End, string Source,
    string Shape, string Role, List<ExpressionSlot> Slots);
public sealed record UiGroup(string Key, string Kind, List<UiText> Texts);
public sealed record TranslationEntry(string Key, string Source, string Translation, string Kind = "", string Role = "", string Shape = "");
public sealed record TranslationPackage(List<TranslationEntry> Entries, List<string>? RetainedSources = null);
public sealed record CatalogGroup(string Id, string Key, string Kind, List<CatalogUnit> Units);
public sealed record CatalogUnit(string Id, string Key, string Source, string Role = "", string Shape = "");
public sealed record TranslationCatalog(List<CatalogGroup> Groups);
public sealed record ScanResult(string Version, List<UiGroup> Groups, List<string> Notices, string AdapterSignature);
public sealed record MaintenanceDecision(bool NeedsUpdate, int NewContexts, int MissingTranslations, List<string> Notices, string AdapterSignature);
public sealed record WorkProgress(string Stage, string Message, double? Percent = null);
public sealed record FileChange(string RelativePath, string OriginalHash, string PatchedHash);
public sealed record PatchReceipt(string Root, string Version, string Phase, List<FileChange> Files,
    string? BundleHash, int TranslatedTexts, int RetainedTexts, List<string> Notices, string PackageHash, string ServerHash);
public sealed record PreparedPatch(Installation Installation, string Directory, string BackupDirectory,
    string ExpectedAsarHash, string ExpectedExeHash, string ExpectedServerHash, string? ExpectedBundleHash, PatchReceipt Receipt);
public sealed record HelperResult(bool Success, string Message);
