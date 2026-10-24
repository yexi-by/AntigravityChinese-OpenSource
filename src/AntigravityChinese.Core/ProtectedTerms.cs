using System.Text.RegularExpressions;

namespace AntigravityChinese.Core;

public static partial class ProtectedTerms
{
    // 仅辅助判断纯专名原文是否需要翻译，不作为占位符或译文的强制词汇约束。
    public const string Pattern = @"(?i)`[^`\r\n]+`|file:///[^\s)）]+|https?://[^\s<>]+|(?<![a-z0-9_])(?:Docker(?: Desktop)?|GitHub|GitLab|Bitbucket|Antigravity(?: IDE| App)?|Google(?: Drive| Docs| Cloud| Chrome)?|Gemini(?:\s+\d+(?:\.\d+)*(?:\s+(?:Pro|Flash(?:-Lite)?|Deep Think|Nano))?)?|Claude(?:\s+(?:Sonnet|Opus|Haiku))?(?:\s+\d+(?:\.\d+)*)?|Codex|OpenAI|Chrome|Chromium|Firefox|Safari|Microsoft Edge|Windows|Linux|macOS|Ubuntu|Debian|WSL(?:2)?|JavaScript|TypeScript|Python|Rust|React|Node(?:\.js)?|npm|pnpm|Git|Kubernetes|tokens?|API|SDK|CLI|IDE|MCPs?|HTTP(?:S)?|SSH|OAuth|JSONL?|YAML|TOML|HTML|CSS|SQL|gRPC|RPC|UUID|SHA-?256|UTF-?8|VS Code|Visual Studio Code|PDFs?|CSV|TSV|SVG|PNG|JPEG|JPG|WebP|GIF|MP3|MP4|WAV|CPU|GPU|RAM|PID|URLs?|URIs?|JWT|GraphQL|WebSocket|OpenAPI|Ctrl(?:\s*\+\s*[a-z0-9]+)*|Cmd(?:\s*\+\s*[a-z0-9]+)*|Alt(?:\s*\+\s*[a-z0-9]+)*|Shift(?:\s*\+\s*[a-z0-9]+)*)(?![a-z0-9_])";

    public static bool NeedsTranslation(string text) => Regex.IsMatch(JavaScriptUi.Slots().Replace(Terms().Replace(text, ""), ""), "[A-Za-z]{2}");

    [GeneratedRegex(Pattern)] public static partial Regex Terms();
}
