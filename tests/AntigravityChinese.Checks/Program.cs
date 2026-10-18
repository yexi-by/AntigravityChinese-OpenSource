using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntigravityChinese.Core;

var checks = new (string Name, Action Run)[]
{
    ("界面替换保留业务字段、动态求值和专业名称", CheckJavaScript),
    ("构建位置变化复用译文，未知文案保持原样", CheckStableIdentity),
    ("配置标签与状态函数只在显示处汉化，内部值和求值次数保持", CheckDisplayValues),
    ("组件参数、作用域和界面配置保留身份及可编辑内容", CheckUiConfiguration),
    ("服务配额文案显示中文，保留数据、型号与未知句子", CheckServiceDisplay),
    ("未知版本安装、重复汉化和恢复保持资源完整", CheckInstall),
    ("写入失败回滚，更新后拒绝旧备份覆盖", CheckRecovery),
    ("原生加载适配绑定本机服务并保留 WSL 行为", CheckNativeHook)
};
try
{
    foreach (var (name, run) in checks) { run(); Console.WriteLine("通过：" + name); }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }

static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Reject(Action run)
{
    try { run(); } catch (Exception e) when (e is IOException or InvalidDataException) { return; }
    throw new Exception("预期操作应安全停止。");
}
static string Node(string code)
{
    var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("-"); using var process = Process.Start(start)!;
    process.StandardInput.Write(code); process.StandardInput.Close();
    var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception(error); return output.Trim();
}
static void CheckJavaScript()
{
    const string code = "let effects=0;const load=()=>++effects;const business={label:'Delete',message:'Refresh GitHub token'};const React={createElement:(tag,props,...children)=>({tag,props,children})};const style=React.createElement('style',null,'body { color: red; }');const example=React.createElement('code',null,'git commit');const highlighted=React.createElement('pre',null,React.createElement('code',null,React.createElement('span',{className:'variable'},'name')));const payload=React.createElement('component',{language:'typescript',text:'const name = value;'});const view=React.createElement('button',{title:'Refresh GitHub token',onClick:()=>effects++},`Used ${load()} tokens`);view.props.onClick();console.log(JSON.stringify({view,business,effects,style,example,highlighted,payload}));";
    var groups = JavaScriptUi.Extract(code, "web/main.js");
    var texts = groups.SelectMany(g => g.Texts).ToList();
    Assert(!texts.Any(t => t.Source is "name" or "const name = value;"), "高亮代码或代码组件载荷进入了普通界面翻译。");
    var map = texts.ToDictionary(t => t.Key, t => t.Source == "Refresh GitHub token" ? "刷新 GitHub token" : "已使用 ⟪AGC0⟫ tokens");
    var patched = JavaScriptUi.Apply(code, texts, map);
    using var result = JsonDocument.Parse(Node(patched)); var root = result.RootElement;
    Assert(root.GetProperty("effects").GetInt32() == 2, "动态表达式或事件副作用发生变化。");
    Assert(root.GetProperty("business").GetProperty("message").GetString() == "Refresh GitHub token", "误改业务字段。");
    Assert(root.GetProperty("style").GetProperty("children")[0].GetString() == "body { color: red; }" && root.GetProperty("example").GetProperty("children")[0].GetString() == "git commit", "误翻译样式或代码示例。");
    Assert(root.GetProperty("highlighted").GetProperty("children")[0].GetProperty("children")[0].GetProperty("children")[0].GetString() == "name" &&
        root.GetProperty("payload").GetProperty("props").GetProperty("text").GetString() == "const name = value;", "高亮代码或代码组件载荷发生改变。");
    const string tokenCode = "const A={createElement:(tag,props,...children)=>({children})};const tokens=[{text:'name',color:'variable'}];const preview=A.createElement('div',{className:'font-mono text-sm',style:{whiteSpace:'pre'}},tokens.map(token=>A.createElement('span',null,token.text)));console.log(JSON.stringify(preview));";
    Assert(JavaScriptUi.Extract(tokenCode, "web/main.js").Count == 0, "以 div 和语法 token 绘制的代码预览被当成普通界面文本。");
    Assert(root.GetProperty("view").GetProperty("props").GetProperty("title").GetString() == "刷新 GitHub token", "界面标题未正确显示。");
    Assert(root.GetProperty("view").GetProperty("children")[0].GetString() == "已使用 1 tokens", "动态文案改变。");
    var template = texts.Single(t => t.Shape == "template"); map[template.Key] = "已使用 tokens";
    Reject(() => JavaScriptUi.Apply(code, texts, map));
    Reject(() => ProtectedTerms.Validate("Refresh GitHub token", "刷新 GitHub 令牌"));
}
static void CheckStableIdentity()
{
    const string code = "A.createElement('button',{title:'Copy'},'Copy');";
    var first = JavaScriptUi.Extract(code, "web/main-oldhash.js").SelectMany(g => g.Texts).ToList();
    var nextCode = "const release=101;\n" + code + "A.createElement('div',null,'New upstream feature');";
    var next = JavaScriptUi.Extract(nextCode, "web/main-newhash.js").SelectMany(g => g.Texts).ToList();
    var map = first.DistinctBy(t => t.Key).ToDictionary(t => t.Key, _ => "复制");
    var patched = JavaScriptUi.Apply(nextCode, next, map);
    Assert(patched.Contains("New upstream feature", StringComparison.Ordinal) && patched.Contains("复制", StringComparison.Ordinal), "新版本没有部分应用并保留新增原文。");
    Assert(first.Select(t => t.Key).SequenceEqual(next.Take(first.Count).Select(t => t.Key)), "构建位置进入了长期身份。");
    var originalGroup = JavaScriptUi.Extract(code, "web/main.js").Single();
    var package = new TranslationPackage(originalGroup.Texts.DistinctBy(t => t.Key)
        .Select(t => new TranslationEntry(t.Key, t.Source, "复制", originalGroup.Kind, t.Role, t.Shape)).ToList());
    var changed = JavaScriptUi.Extract("A.createElement('button',{title:'Copy'},'Copy','Added upstream');", "web/new.js").Single();
    var matcher = new TranslationMatcher(package);
    Assert(changed.Texts.Where(t => t.Source == "Copy").All(t => matcher.Find(t, changed.Kind) == "复制") &&
        matcher.Find(changed.Texts.Single(t => t.Source == "Added upstream"), changed.Kind) is null, "相邻新文案阻断了可复用的已知译文。");
    const string moved = "const A={createElement:(tag,props,...children)=>({props,children})};const label='Copy';const view=A.createElement('button',{title:label},label);console.log(JSON.stringify({view,label}));";
    var movedGroups = JavaScriptUi.Extract(moved, "web/new.js"); var movedTexts = movedGroups.SelectMany(g => g.Texts).ToList();
    var movedMap = movedGroups.SelectMany(g => g.Texts.Select(t => (Text: t, Translation: matcher.Find(t, g.Kind))))
        .Where(p => p.Translation is not null).DistinctBy(p => p.Text.Key).ToDictionary(p => p.Text.Key, p => p.Translation!);
    using var movedResult = JsonDocument.Parse(Node(JavaScriptUi.Apply(moved, movedTexts, movedMap)));
    Assert(movedResult.RootElement.GetProperty("view").GetProperty("children")[0].GetString() == "复制" && movedResult.RootElement.GetProperty("label").GetString() == "Copy", "上游将文案移入变量后失去已有译文，或改变了内部值。");
    package.Entries.Add(package.Entries.First() with { Key = "another-context", Translation = "复制内容" });
    Assert(new TranslationMatcher(package).Find(changed.Texts.First(t => t.Source == "Copy" && t.Role == package.Entries[0].Role), changed.Kind) is null,
        "有歧义的跨语境译文仍被盲目复用。");
}
static void CheckInstall()
{
    using var fixture = new Fixture();
    var originalAsar = AsarArchive.FileHash(fixture.App.Asar); var originalServer = AsarArchive.FileHash(fixture.App.Server);
    var prepared = fixture.Prepare(); Assert(prepared.Receipt.BundleHash is not null, "未生成主界面原生资源包。"); PatchInstaller.Apply(prepared);
    using (var asar = new AsarArchive(fixture.App.Asar))
        Assert(asar.ReadText("dist/menu.js").Contains("复制", StringComparison.Ordinal), "桌面菜单未写入。");
    Assert(PatchInstaller.FindApplied(fixture.App, fixture.Data) is not null, "应用状态未确认。");
    var hash = AsarArchive.FileHash(fixture.App.Asar); PatchInstaller.Apply(fixture.Prepare());
    Assert(AsarArchive.FileHash(fixture.App.Asar) == hash, "重复汉化叠加了补丁。");
    Assert(AsarArchive.FileHash(fixture.App.Server) == originalServer, "修改了本机服务二进制。");
    File.AppendAllText(fixture.App.Server, "upstream server update");
    var updatedServer = AsarArchive.FileHash(fixture.App.Server);
    var refreshed = fixture.Prepare(); PatchInstaller.Apply(refreshed);
    Assert(refreshed.Receipt.ServerHash == updatedServer && refreshed.Receipt.ServerHash != prepared.Receipt.ServerHash,
        "本机服务单独更新后没有重新绑定中文资源。");
    PatchInstaller.Restore(fixture.App, fixture.Data);
    Assert(AsarArchive.FileHash(fixture.App.Asar) == originalAsar, "恢复没有还原原始资源。");
    Assert(!File.Exists(Path.Combine(fixture.Root, NativeBundleAdapter.ResourceDirectory, "web.zip")), "恢复后残留活动资源包。");
    Assert(AsarArchive.FileHash(fixture.App.Server) == updatedServer, "恢复覆盖了更新后的本机服务。");
    using var changedLoader = new Fixture("function upstream(){return ['--new-launch-format'];}");
    var partial = changedLoader.Prepare(); PatchInstaller.Apply(partial);
    using var partialAsar = new AsarArchive(changedLoader.App.Asar);
    Assert(partial.Receipt.BundleHash is null && partial.Receipt.Notices.Count > 0 &&
        partialAsar.ReadText("dist/menu.js").Contains("复制", StringComparison.Ordinal) &&
        partialAsar.ReadText("dist/languageServer.js").Contains("--new-launch-format", StringComparison.Ordinal),
        "新版本启动结构变化时，没有保留原版前端并应用可用的桌面译文。");
}
static void CheckDisplayValues()
{
    const string code = "const A={createElement:(tag,props,...children)=>({tag,props,children})};const choices=[{value:'Always',label:'Always'},{value:'Never',label:'Never'}];let effects=0;function status(){effects++;return true?'Ready':'Busy';}const options=choices.map(item=>A.createElement('option',{value:item.value},item.label));const state=A.createElement('span',null,status());console.log(JSON.stringify({choices,options,state,effects}));";
    var groups = JavaScriptUi.Extract(code, "web/main.js");
    var dictionary = new Dictionary<string, string> { ["Always"] = "总是", ["Never"] = "从不", ["Ready"] = "就绪", ["Busy"] = "忙碌" };
    var texts = groups.SelectMany(g => g.Texts).ToList(); var map = texts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => dictionary[t.Source]);
    using var parsed = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var value = parsed.RootElement;
    Assert(value.GetProperty("choices")[0].GetProperty("label").GetString() == "Always", "修改了配置数据。");
    Assert(value.GetProperty("options")[0].GetProperty("props").GetProperty("value").GetString() == "Always", "修改了选项内部值。");
    Assert(value.GetProperty("options")[0].GetProperty("children")[0].GetString() == "总是", "未汉化实际选项标签。");
    Assert(value.GetProperty("state").GetProperty("children")[0].GetString() == "就绪" && value.GetProperty("effects").GetInt32() == 1, "状态显示或求值次数发生变化。");
}
static void CheckServiceDisplay()
{
    const string code = """
    const UI={createElement:(tag,props,...children)=>typeof tag==='function'?tag(props):({props,children})};
    function Quota({label:caption,remainingFraction:ratio,refreshText:hint,disabled:off}){
      return UI.createElement('row',{label:UI.createElement('span',null,caption),description:hint,ratio});
    }
    function Group({group:payload,sharedModels:names}){
      const exists=payload.buckets&&payload.buckets.length;
      return UI.createElement('section',{title:UI.createElement('span',null,payload.displayName)},
        payload.buckets.map(item=>UI.createElement(Quota,{label:item.displayName,remainingFraction:item.fraction,refreshText:item.description,disabled:false})));
    }
    const data=JSON.parse('{"displayName":"Gemini Models","buckets":[{"displayName":"Weekly Limit Remaining","fraction":1},{"displayName":"Five Hour Limit Remaining","fraction":0.25},{"displayName":"Future Model $& Weekly Limit","fraction":0.92,"description":"You have used some of your weekly limit, it will fully refresh in 6 days, 19 hours."},{"displayName":"Experimental Rolling Limit","fraction":0.42,"description":"A new service phrase."}]}');
    let calls=0;Object.defineProperty(data,'displayName',{enumerable:true,get(){calls++;return 'Gemini Models';}});
    const view=Group({group:data});const evaluated=calls;
    function Plan(state){const text=state?.userTier?.upgradeSubscriptionText||'';return UI.createElement('row',{description:text});}
    const account=JSON.parse('{"userTier":{"upgradeSubscriptionText":"You can upgrade to a Google AI Ultra plan to receive higher rate limits."}}');
    var editable='Weekly Limit Remaining';function edit(value){editable=value;}
    function Reused({label:caption,remainingFraction:ratio,refreshText:hint,disabled:off}){caption=editable;return UI.createElement('span',null,caption);}
    const outside=UI.createElement('span',null,editable),node={title:'A node'};
    const unchanged=Quota({label:null,refreshText:node});
    const singular=Quota({label:'Gemini 4 Argon Weekly Limit',refreshText:'You have used some of your weekly limit, it will fully refresh in 1 day, 2 hours and 3 minutes.'});
    const unknown=Quota({label:'Gemini 4 Argon',refreshText:'You have used some of your weekly limit, it will fully refresh in tomorrow.'});
    console.log(JSON.stringify({title:view.props.title.children[0],rows:view.children[0].map(row=>({label:row.props.label.children[0],description:row.props.description,fraction:row.props.ratio})),
      evaluated,data,account,plan:Plan(account).props.description,outside:outside.children[0],reused:Reused({}).children[0],nodeUnchanged:unchanged.props.description===node,singular:singular.props.description,unknown:unknown.props}));
    """;
    var translations = new Dictionary<string, string>
    {
        ["Gemini Models"] = "Gemini 模型", ["Claude and GPT models"] = "Claude 和 GPT 模型",
        ["Weekly Limit Remaining"] = "本周剩余额度", ["Five Hour Limit Remaining"] = "5 小时剩余额度", ["⟪AGC0⟫ Weekly Limit"] = "⟪AGC0⟫ 每周额度",
        ["You have used some of your weekly limit, it will fully refresh in ⟪AGC0⟫."] = "你已使用部分每周额度，将在 ⟪AGC0⟫后完全恢复。",
        ["You can upgrade to a ⟪AGC0⟫ plan to receive higher rate limits."] = "升级至 ⟪AGC0⟫ 套餐可获得更高的使用额度。",
        ["⟪AGC0⟫ day"] = "⟪AGC0⟫ 天", ["⟪AGC0⟫ days"] = "⟪AGC0⟫ 天", ["⟪AGC0⟫ hour"] = "⟪AGC0⟫ 小时", ["⟪AGC0⟫ hours"] = "⟪AGC0⟫ 小时",
        ["⟪AGC0⟫ minute"] = "⟪AGC0⟫ 分钟", ["⟪AGC0⟫ minutes"] = "⟪AGC0⟫ 分钟", ["⟪AGC0⟫ second"] = "⟪AGC0⟫ 秒", ["⟪AGC0⟫ seconds"] = "⟪AGC0⟫ 秒"
    };
    var texts = JavaScriptUi.Extract(code, "web/future-build.js").SelectMany(g => g.Texts).ToList();
    var map = texts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => translations[t.Source]);
    using var result = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var value = result.RootElement; var rows = value.GetProperty("rows");
    Assert(value.GetProperty("title").GetString() == "Gemini 模型" && rows[0].GetProperty("label").GetString() == "本周剩余额度" && rows[1].GetProperty("label").GetString() == "5 小时剩余额度", "服务下发的配额显示名称没有汉化。");
    Assert(rows[2].GetProperty("label").GetString() == "Future Model $& 每周额度" && rows[2].GetProperty("description").GetString() == "你已使用部分每周额度，将在 6 天 19 小时后完全恢复。", "型号内容或倒计时单位处理错误。");
    Assert(value.GetProperty("singular").GetString() == "你已使用部分每周额度，将在 1 天 2 小时 3 分钟后完全恢复。" && value.GetProperty("plan").GetString() == "升级至 Google AI Ultra 套餐可获得更高的使用额度。", "单复数时长或套餐名称处理错误。");
    Assert(value.GetProperty("evaluated").GetInt32() == 1 && value.GetProperty("data").GetProperty("displayName").GetString() == "Gemini Models" && rows[2].GetProperty("fraction").GetDouble() == 0.92 && value.GetProperty("account").GetProperty("userTier").GetProperty("upgradeSubscriptionText").GetString()!.StartsWith("You can upgrade"), "显示汉化改变了接口数据、求值次数或配额数值。");
    Assert(value.GetProperty("outside").GetString() == "Weekly Limit Remaining" && value.GetProperty("reused").GetString() == "Weekly Limit Remaining" && value.GetProperty("nodeUnchanged").GetBoolean() && rows[3].GetProperty("description").GetString() == "A new service phrase." && value.GetProperty("unknown").GetProperty("label").GetProperty("children")[0].GetString() == "Gemini 4 Argon" && value.GetProperty("unknown").GetProperty("description").GetString()!.EndsWith("tomorrow."), "误改用户内容、重新赋值的参数、模型名、非字符串节点或未知文案。");
}
static void CheckRecovery()
{
    using var fixture = new Fixture(); var original = AsarArchive.FileHash(fixture.App.Asar);
    var originalBytes = File.ReadAllBytes(fixture.App.Asar);
    var prepared = fixture.Prepare();
    using (var locked = new FileStream(fixture.App.Executable, FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => PatchInstaller.Apply(prepared));
    Assert(AsarArchive.FileHash(fixture.App.Asar) == original, "第二个文件失败后未恢复第一个文件。");
    PatchInstaller.Apply(fixture.Prepare());
    File.WriteAllBytes(fixture.App.Asar, originalBytes.Concat("upstream update"u8.ToArray()).ToArray());
    var changed = AsarArchive.FileHash(fixture.App.Asar);
    Reject(() => PatchInstaller.Restore(fixture.App, fixture.Data));
    Assert(AsarArchive.FileHash(fixture.App.Asar) == changed, "旧备份覆盖了更新后的文件。");
    PatchInstaller.Apply(fixture.Prepare());
    Assert(PatchInstaller.FindApplied(fixture.App, fixture.Data) is not null, "更新后残留的自有中文包阻止了重新汉化。");
    PatchInstaller.Restore(fixture.App, fixture.Data);
    Assert(AsarArchive.FileHash(fixture.App.Asar) == changed, "重新汉化后恢复到了旧版原件。");
}
static void CheckUiConfiguration()
{
    const string code = "var A={createElement:(tag,props,...children)=>({props,children})};var command={id:'Copy',label:'Copy',keybindings:{key:'c'}};var setting={isProviderSetting:true,settingType:'switch',screen:'General',label:'Default',description:'Refresh GitHub token'};var editable='Copy';function edit(value){editable=value;}function build(){var terms=()=>({plural:'Projects'});var {plural}=terms();return ()=>A.createElement('div',{text:command.label,tooltipText:'Copy'},plural,editable);}console.log(JSON.stringify({command,setting,view:build()()}));";
    var texts = JavaScriptUi.Extract(code, "web/main.js").SelectMany(g => g.Texts).ToList();
    var labels = new Dictionary<string, string> { ["Copy"] = "复制", ["Default"] = "默认", ["Refresh GitHub token"] = "刷新 GitHub token", ["Projects"] = "项目" };
    var map = texts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => labels[t.Source]);
    using var result = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var json = result.RootElement;
    Assert(json.GetProperty("command").GetProperty("id").GetString() == "Copy" && json.GetProperty("command").GetProperty("keybindings").GetProperty("key").GetString() == "c", "汉化改变了命令身份或快捷键。");
    Assert(json.GetProperty("setting").GetProperty("screen").GetString() == "General" && json.GetProperty("setting").GetProperty("label").GetString() == "默认", "设置显示标签和内部页面身份未分离。");
    Assert(json.GetProperty("view").GetProperty("props").GetProperty("text").GetString() == "复制" && json.GetProperty("view").GetProperty("children")[0].GetString() == "项目", "跨作用域和解构后的真实标签没有显示中文。");
    Assert(json.GetProperty("view").GetProperty("children")[1].GetString() == "Copy", "误翻译了有写入来源的可编辑内容。");
    const string screenCode = "const A={useMemo:f=>f(),createElement:(tag,props,...children)=>({props,children})};const screens=['General','Models'];const visible=A.useMemo(()=>screens.filter(s=>s!=='Models'),[]);const nav=visible.map(screen=>A.createElement('button',{onClick:()=>screen},screen));console.log(JSON.stringify({screens,nav:nav.map(n=>({label:n.children[0],target:n.props.onClick()}))}));";
    var screenTexts = JavaScriptUi.Extract(screenCode, "web/main.js").SelectMany(g => g.Texts).ToList();
    var screenMap = screenTexts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => t.Source == "General" ? "常规" : "模型");
    using var navigation = JsonDocument.Parse(Node(JavaScriptUi.Apply(screenCode, screenTexts, screenMap)));
    Assert(navigation.RootElement.GetProperty("screens")[0].GetString() == "General" && navigation.RootElement.GetProperty("nav")[0].GetProperty("target").GetString() == "General" &&
        navigation.RootElement.GetProperty("nav")[0].GetProperty("label").GetString() == "常规", "筛选与缓存后的设置导航改变了路由身份，或没有汉化显示标签。");
    const string registryCode = "const screens=[{screen:'General'},{screen:'App',label:'Application'}];const labels=new Map(screens.filter(s=>s.label!==undefined).map(s=>[s.screen,s.label]));var name=()=>0;name=true?'Projects':'Workspaces';var caption=true?`${name} (Archived)`:name;name=()=>1;const header={title:caption,headerActions:()=>0};console.log(JSON.stringify({screens,caption,title:header.title,label:labels.get('General')}));";
    var registryTexts = JavaScriptUi.Extract(registryCode, "web/main.js").SelectMany(g => g.Texts).ToList();
    var registryMap = registryTexts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => t.Source switch
        { "General" => "常规", "Application" => "应用", "Projects (Archived)" => "项目（已归档）", "Workspaces (Archived)" => "工作区（已归档）", "Projects" => "项目", "Workspaces" => "工作区", _ => throw new Exception(t.Source) });
    using var registry = JsonDocument.Parse(Node(JavaScriptUi.Apply(registryCode, registryTexts, registryMap)));
    Assert(registry.RootElement.GetProperty("screens")[0].GetProperty("screen").GetString() == "General" && registry.RootElement.GetProperty("label").GetString() == "常规", "补充显示标签改变了设置路由，或标签映射没有生效。");
    Assert(registry.RootElement.GetProperty("caption").GetString() == "Projects (Archived)" && registry.RootElement.GetProperty("title").GetString() == "项目（已归档）", "复用变量后的分组标题改变了上游状态，或没有显示中文。");
    const string optionCode = "const setting={isProviderSetting:true,settingType:'select',label:'Policy',resolveOptionToString:choice=>{switch(choice){case 1:return 'Always Ask';default:return 'Never';}}};function placeholder({inputPlaceholder:custom,supportsWorkflows:enabled}){return custom?custom:'Ask anything'+(enabled?', / for actions':'');}console.log(JSON.stringify({choice:1,label:setting.resolveOptionToString(1),defaultInput:placeholder({supportsWorkflows:true}),customInput:placeholder({inputPlaceholder:'Ask anything'})}));";
    var optionTexts = JavaScriptUi.Extract(optionCode, "web/main.js").SelectMany(g => g.Texts).ToList();
    var optionLabels = new Dictionary<string, string> { ["Policy"] = "策略", ["Always Ask"] = "始终询问", ["Never"] = "从不", ["Ask anything"] = "想问什么都可以", [", / for actions"] = "，输入 / 执行操作" };
    var optionMap = optionTexts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => optionLabels[t.Source]);
    using var options = JsonDocument.Parse(Node(JavaScriptUi.Apply(optionCode, optionTexts, optionMap)));
    Assert(options.RootElement.GetProperty("choice").GetInt32() == 1 && options.RootElement.GetProperty("label").GetString() == "始终询问", "选项显示格式化改变了内部策略值。");
    Assert(options.RootElement.GetProperty("defaultInput").GetString() == "想问什么都可以，输入 / 执行操作" && options.RootElement.GetProperty("customInput").GetString() == "Ask anything", "默认占位提示与用户自定义内容未分离。");
}
static void CheckNativeHook()
{
    using var fixture = new Fixture(); var directory = Path.Combine(fixture.Root, NativeBundleAdapter.ResourceDirectory);
    Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "web.zip"), "fixture");
    var hook = Path.Combine(fixture.Root, "hook.cjs"); File.WriteAllBytes(hook, NativeBundleAdapter.CreateHook(fixture.App, AsarArchive.FileHash(Path.Combine(directory, "web.zip")), AsarArchive.FileHash(fixture.App.Server)));
    var root = JavaScriptUi.Quote(Path.Combine(fixture.Root, "resources")); var module = JavaScriptUi.Quote(hook);
    var output = Node($"Object.defineProperty(process,'resourcesPath',{{value:{root}}});const m=require({module}),fs=require('fs');let a=[],b=[];m.addBundle(a,false);m.addBundle(b,true);const binary=require('path').join({root},'bin','language_server.exe'),stat=fs.statSync(binary),bytes=fs.readFileSync(binary);bytes[0]^=1;fs.writeFileSync(binary,bytes);fs.utimesSync(binary,stat.atime,stat.mtime);let c=[];m.addBundle(c,false);console.log(JSON.stringify([a,b,c]));");
    using var parsed = JsonDocument.Parse(output);
    Assert(parsed.RootElement[0][0].GetString() == "--web_bundle_path" && parsed.RootElement[1].GetArrayLength() == 0 &&
           parsed.RootElement[2].GetArrayLength() == 0, "本机服务内容变化后仍加载了旧中文包，或 WSL 回退失效。");
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetFullPath("work/checks"), Guid.NewGuid().ToString("N"));
    public string Data => Path.Combine(Root, "state");
    public Installation App { get; }
    public TranslationPackage Package { get; }
    public Fixture(string loader = "function start(options){const {wsl}=options;const args=['--standalone','--subclient_type','hub','--csrf_token'];return args;}")
    {
        Directory.CreateDirectory(Path.Combine(Root, "resources", "bin"));
        File.Copy(Environment.ProcessPath!, Path.Combine(Root, "Antigravity.exe"));
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new Dictionary<string, string> { ["index.html"] = "<script src=\"/main.js\"></script>",
                         ["main.js"] = "A.createElement('button',null,'Copy');", ["unchanged.bin"] = "untouched resource" })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        using (var server = File.Create(Path.Combine(Root, "resources", "bin", "language_server.exe")))
        { server.Write("MZ fixture web_bundle_path"u8); server.Write(zipBytes.ToArray()); server.Write("suffix"u8); }
        WriteAsar(Path.Combine(Root, "resources", "app.asar"), new()
        {
            ["package.json"] = "{\"name\":\"antigravity\",\"version\":\"99.99.0\",\"main\":\"dist/main.js\"}",
            ["dist/menu.js"] = "const menu=[{label:'Copy',role:'copy',id:'Copy'}];",
            ["dist/languageServer.js"] = loader
        });
        App = AppLocator.Inspect(Root);
        var scan = SourceScanner.Scan(App);
        Package = new(scan.Groups.SelectMany(g => g.Texts).DistinctBy(t => t.Key).Select(t => new TranslationEntry(t.Key, t.Source, "复制")).ToList());
    }
    public PreparedPatch Prepare() => PatchBuilder.Prepare(App, Package, Path.Combine(Root, "prepare-" + Guid.NewGuid().ToString("N")), Data);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    private static void WriteAsar(string path, Dictionary<string, string> files)
    {
        var header = new JsonObject { ["files"] = new JsonObject() }; var offset = 0;
        foreach (var (name, text) in files)
        {
            var parts = name.Split('/'); var directory = header["files"]!.AsObject();
            foreach (var part in parts[..^1]) { directory[part] ??= new JsonObject { ["files"] = new JsonObject() }; directory = directory[part]!["files"]!.AsObject(); }
            var bytes = Encoding.UTF8.GetBytes(text); directory[parts[^1]] = new JsonObject { ["offset"] = offset.ToString(), ["size"] = bytes.Length }; offset += bytes.Length;
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(header); var padded = (json.Length + 3) / 4 * 4;
        using var output = new BinaryWriter(File.Create(path)); output.Write(4u); output.Write((uint)(8 + padded)); output.Write((uint)(4 + padded)); output.Write((uint)json.Length);
        output.Write(json); output.Write(new byte[padded - json.Length]); foreach (var text in files.Values) output.Write(Encoding.UTF8.GetBytes(text));
    }
}
