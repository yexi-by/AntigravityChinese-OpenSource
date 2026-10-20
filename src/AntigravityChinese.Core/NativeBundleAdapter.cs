using System.Text;
using System.Text.Json;
using Acornima;
using Acornima.Ast;

namespace AntigravityChinese.Core;

public static class NativeBundleAdapter
{
    public const string HookFile = "dist/antigravity-chinese.js";
    public const string ResourceDirectory = "resources/antigravity-chinese";
    public const string HookMarker = "/* antigravity-chinese */";

    public static (string Code, string Signature)? Attach(string code)
    {
        if (code.Contains(HookMarker, StringComparison.Ordinal)) throw new InvalidDataException("资源中已经存在汉化加载适配，请先按当前备份恢复。");
        var root = new Parser().ParseScript(code);
        var matches = new List<(VariableDeclaration Declaration, string Variable)>();
        var functions = new List<Node>();
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is FunctionDeclaration or FunctionExpression or ArrowFunctionExpression) functions.Add(node);
            if (node is VariableDeclaration declaration)
                foreach (var variable in declaration.Declarations)
                    if (variable is { Id: Identifier id, Init: ArrayExpression array })
                    {
                        var literals = array.Elements.OfType<StringLiteral>().Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
                        if (literals.IsSupersetOf(["--standalone", "--subclient_type", "hub", "--csrf_token"]))
                            matches.Add((declaration, id.Name));
                    }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        if (matches.Count != 1) return null;
        var match = matches[0];
        var bindings = new List<string>();
        foreach (var function in functions.Where(f => f.Start <= match.Declaration.Start && f.End >= match.Declaration.End).OrderBy(f => f.End - f.Start))
        {
            pending.Push(function);
            while (pending.TryPop(out var node))
            {
                if (node is VariableDeclarator { Id: ObjectPattern pattern })
                    foreach (var property in pattern.Properties.OfType<AssignmentProperty>())
                        if (property.Key is Identifier { Name: "wsl" } && property.Value is Identifier value) bindings.Add(value.Name);
                if (node != function && node is (FunctionDeclaration or FunctionExpression or ArrowFunctionExpression)) continue;
                foreach (var child in node.ChildNodes) pending.Push(child);
            }
            if (bindings.Count > 0) break;
        }
        if (bindings.Distinct().Count() != 1) return null;
        var insert = $"\n{HookMarker}\ntry {{ require('./antigravity-chinese').addBundle({match.Variable}, !!{bindings[0]}); }} catch {{ /* 读取失败时使用内置原版界面。 */ }}\n";
        var patched = code.Insert(match.Declaration.End, insert);
        JavaScriptUi.Validate(patched);
        return (patched, "native-web-bundle/" + string.Join(',', new[] { "--standalone", "--subclient_type", "hub", "--csrf_token" }));
    }

    public static byte[] CreateHook(Installation installation, string bundlePath, string serverHash)
    {
        var server = new FileInfo(installation.Server);
        var bundleHash = AsarArchive.FileHash(bundlePath);
        var files = ExpandedWebBundle.ReadHashes(bundlePath);
        var directories = files.Keys.SelectMany(name => name.Select((c, i) => c == '/' ? name[..i] : null))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // 与生成资源的本机服务绑定。独立替换服务或改变时间戳后自动使用原版界面。
        var stamp = new DateTimeOffset(server.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var code = $$"""
        'use strict';
        const fs = require('node:fs');
        const path = require('node:path');
        const crypto = require('node:crypto');
        function fileHash(file, buffer) {
          const handle = fs.openSync(file, 'r');
          try {
            const hash = crypto.createHash('sha256');
            let count;
            while ((count = fs.readSync(handle, buffer, 0, buffer.length, null)) > 0) hash.update(buffer.subarray(0, count));
            return hash.digest('hex');
          } finally { fs.closeSync(handle); }
        }
        exports.addBundle = function(args, wsl) {
          if (wsl || args.includes('--web_bundle_path')) return;
          try {
            const root = process.resourcesPath;
            const binaryPath = path.join(root, 'bin', 'language_server.exe');
            const binary = fs.statSync(binaryPath);
            const bundle = path.join(root, 'antigravity-chinese', 'web.zip');
            const expanded = path.join(root, 'antigravity-chinese', 'web-{{bundleHash}}');
            const buffer = Buffer.allocUnsafe(256 * 1024);
            const files = {{JsonSerializer.Serialize(files)}};
            const directories = {{JsonSerializer.Serialize(directories)}};
            if (binary.size === {{server.Length}} && Math.floor(binary.mtimeMs) === {{stamp}} && fs.statSync(bundle).isFile() &&
                fileHash(binaryPath, buffer) === '{{serverHash}}' && fileHash(bundle, buffer) === '{{bundleHash}}' &&
                fs.lstatSync(path.dirname(expanded)).isDirectory() && fs.lstatSync(expanded).isDirectory() &&
                directories.every(name => fs.lstatSync(path.join(expanded, name)).isDirectory()) &&
                Object.entries(files).every(([name, hash]) => {
                  const file = path.join(expanded, name);
                  return fs.lstatSync(file).isFile() && fileHash(file, buffer) === hash;
                })) {
              args.push('--web_bundle_path', expanded);
            }
          } catch { /* 原版资源始终是回退路径。 */ }
        };
        """;
        JavaScriptUi.Validate(code); return Encoding.UTF8.GetBytes(code);
    }

    public static bool ServerSupportsOverride(string server)
    {
        var bytes = File.ReadAllBytes(server);
        return bytes.AsSpan().IndexOf("web_bundle_path"u8) >= 0;
    }
}
