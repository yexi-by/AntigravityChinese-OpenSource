using System.Text.RegularExpressions;

namespace AntigravityChinese.Core;

public static partial class ProtectedTerms
{
    // 品牌、协议、语言名称及用户明确要求保留的 token；通用操作词不在本表中。
    public const string Pattern = @"(?i)`[^`\r\n]+`|file:///[^\s)）]+|https?://[^\s<>]+|(?<![a-z0-9_])(?:Docker(?: Desktop)?|GitHub|GitLab|Bitbucket|Antigravity(?: IDE| App)?|Google(?: Drive| Docs| Cloud| Chrome)?|Gemini(?:\s+\d+(?:\.\d+)*(?:\s+(?:Pro|Flash(?:-Lite)?|Deep Think|Nano))?)?|Claude(?:\s+(?:Sonnet|Opus|Haiku))?(?:\s+\d+(?:\.\d+)*)?|Codex|OpenAI|Chrome|Chromium|Firefox|Safari|Microsoft Edge|Windows|Linux|macOS|Ubuntu|Debian|WSL(?:2)?|JavaScript|TypeScript|Python|Rust|React|Node(?:\.js)?|npm|pnpm|Git|Kubernetes|tokens?|API|SDK|CLI|IDE|MCPs?|HTTP(?:S)?|SSH|OAuth|JSONL?|YAML|TOML|HTML|CSS|SQL|gRPC|RPC|UUID|SHA-?256|UTF-?8|VS Code|Visual Studio Code|PDFs?|CSV|TSV|SVG|PNG|JPEG|JPG|WebP|GIF|MP3|MP4|WAV|CPU|GPU|RAM|PID|URLs?|URIs?|JWT|GraphQL|WebSocket|OpenAPI|Ctrl(?:\s*\+\s*[a-z0-9]+)*|Cmd(?:\s*\+\s*[a-z0-9]+)*|Alt(?:\s*\+\s*[a-z0-9]+)*|Shift(?:\s*\+\s*[a-z0-9]+)*)(?![a-z0-9_])";

    public static void Validate(string source, string translation)
    {
        var required = Terms().Matches(source).Select(m => m.Value).Order(StringComparer.Ordinal);
        var actual = Terms().Matches(translation).Select(m => m.Value).Order(StringComparer.Ordinal);
        if (!required.SequenceEqual(actual)) throw new InvalidDataException($"品牌或专业名称未按原文保留：{source}");
    }

    public static bool NeedsTranslation(string text) => Regex.IsMatch(JavaScriptUi.Slots().Replace(Terms().Replace(text, ""), ""), "[A-Za-z]{2}");

    [GeneratedRegex(Pattern)] public static partial Regex Terms();
}
