using AntigravityChinese.Core;

try
{
    string Option(string name, string? fallback = null)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException("缺少参数：" + name);
    }
    var command = args.FirstOrDefault() ?? "help";
    if (command == "verify-copy")
    {
        var app = AppLocator.Inspect(Option("--app", AppLocator.Detect()?.Root));
        VerificationCopy.Create(app, Path.GetFullPath(Option("--work")));
        Console.WriteLine("已创建验证副本；文件目录已隔离，副本更新检查已关闭，系统登录服务仍可能复用。"); return 0;
    }
    if (command == "seed-att")
    {
        var count = AttProject.WriteSeedManual(Option("--translation", Path.GetFullPath("translation")),
            JsonFiles.Read<TranslationPackage>(Option("--pack", Path.GetFullPath("packs/zh-CN.json"))), Option("--output"));
        Console.WriteLine($"已生成 {count} 条已发布译文的 ATT 回填文件。"); return 0;
    }
    if (command == "att-resources")
    {
        var directory = Option("--translation", Path.GetFullPath("translation")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "placeholders.toml"),
            "# 由维护工具根据实际安装器的保护规则生成。\n[[rule]]\norder = 'reorder_within_slot'\npattern = '''" + ProtectedTerms.Pattern + "'''\n\n[[rule]]\norder = 'preserve'\npattern = '⟪AGC[0-9]+⟫'\n");
        Console.WriteLine("已生成品牌、专业名称和动态表达式保护规则。"); return 0;
    }
    if (command is "prepare" or "apply" or "restore")
    {
        var installation = AppLocator.Inspect(Option("--app", AppLocator.Detect()?.Root));
        var dataRoot = Option("--data", PatchPaths.DataRoot);
        if (command == "restore") { PatchInstaller.Restore(installation, dataRoot); Console.WriteLine("已恢复原版。"); return 0; }
        var package = JsonFiles.Read<TranslationPackage>(Option("--pack", Path.GetFullPath("packs/zh-CN.json")));
        var directory = Option("--work");
        var prepared = PatchBuilder.Prepare(installation, package, directory, dataRoot,
            new Progress<WorkProgress>(p => Console.Error.WriteLine(p.Message)));
        JsonFiles.Write(Path.Combine(directory, "prepared.json"), prepared);
        if (command == "apply") PatchInstaller.Apply(prepared);
        Console.WriteLine($"{(command == "apply" ? "已安装" : "已准备")}：{prepared.Receipt.TranslatedTexts} 处中文，{prepared.Receipt.RetainedTexts} 处保留原文。");
        foreach (var notice in prepared.Receipt.Notices) Console.Error.WriteLine(notice);
        return 0;
    }
    if (command is "scan" or "extract" or "check")
    {
        var path = Option("--app", AppLocator.Detect()?.Root);
        var installation = AppLocator.Inspect(path);
        var scan = SourceScanner.Scan(installation, new Progress<WorkProgress>(p => Console.Error.WriteLine(p.Message)));
        var root = Option("--translation", Path.GetFullPath("translation"));
        var catalogPath = Path.Combine(root, "catalog.json");
        var known = File.Exists(catalogPath) ? JsonFiles.Read<TranslationCatalog>(catalogPath).Groups.Select(g => g.Key).ToHashSet() : [];
        var added = scan.Groups.DistinctBy(g => g.Key).Count(g => !known.Contains(g.Key));
        if (command == "check")
        {
            var packagePath = Option("--pack", Path.GetFullPath("packs/zh-CN.json"));
            var package = File.Exists(packagePath) ? JsonFiles.Read<TranslationPackage>(packagePath) : new([]);
            var matcher = new TranslationMatcher(package);
            var retained = (package.RetainedSources ?? []).ToHashSet(StringComparer.Ordinal);
            var missing = scan.Groups.SelectMany(g => g.Texts.Select(t => (Text: t, Kind: g.Kind))).DistinctBy(p => p.Text.Key).Count(p =>
                ProtectedTerms.NeedsTranslation(p.Text.Source) && !retained.Contains(p.Text.Source) && matcher.Find(p.Text, p.Kind) is null);
            var newContexts = scan.Groups.DistinctBy(g => g.Key).Count(g => !known.Contains(g.Key) && g.Texts.Any(t => ProtectedTerms.NeedsTranslation(t.Source) && !retained.Contains(t.Source)));
            var decision = new MaintenanceDecision(missing > 0 || scan.Notices.Count > 0, newContexts, missing, scan.Notices, scan.AdapterSignature);
            Console.WriteLine(decision.NeedsUpdate ? $"需要维护：新增 {newContexts} 组，待补译 {missing} 条，结构提示 {scan.Notices.Count} 项。" : "无需更新项目：现有译文和加载方式仍适用。");
            if (Array.IndexOf(args, "--output") >= 0) JsonFiles.Write(Option("--output"), decision);
            return 0;
        }
        Console.WriteLine($"Antigravity App {scan.Version}；界面位置 {scan.Groups.Sum(g => g.Texts.Count)}；新增语境 {added}；加载方式 {scan.AdapterSignature}");
        foreach (var notice in scan.Notices) Console.Error.WriteLine(notice);
        if (command == "extract") Console.WriteLine($"已增加 {AttProject.UpdateInput(scan, root)} 组，其他翻译资产保持不变。");
        var outputIndex = Array.IndexOf(args, "--output");
        if (outputIndex >= 0) JsonFiles.Write(args[outputIndex + 1], scan);
        return 0;
    }
    if (command == "import-att")
    {
        var package = AttProject.Import(Option("--translation", Path.GetFullPath("translation")), Option("--write-back"));
        JsonFiles.Write(Option("--output", Path.GetFullPath("packs/zh-CN.json")), package);
        Console.WriteLine($"已导入 {package.Entries.Count} 条有效译文。"); return 0;
    }
    Console.WriteLine("""
    维护命令（在仓库根目录执行）：
      check / scan / extract [--app 安装目录] [--translation 翻译目录] [--pack 译文包] [--output 结果JSON]
      att-resources [--translation 翻译目录]
      seed-att --output 人工回填TOML [--translation 翻译目录] [--pack 译文包]
      import-att --write-back ATT写回目录 [--translation 翻译目录] [--output 译文包]
    隔离验证与开发：
      verify-copy --work 空的验证目录 [--app 原版安装目录]
      prepare / apply --work 空的准备目录 [--app 安装目录] [--pack 译文包] [--data 备份目录]
      restore [--app 安装目录] [--data 备份目录]
    """);
    return command == "help" ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
