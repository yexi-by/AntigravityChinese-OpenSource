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
    ("项目默认文案、菜单引用与空状态完整汉化，保留项目名称和事件", CheckProjectDisplay),
    ("设置选项与说明只转换最终显示，保留选中映射和权限回调", CheckSettingOptions),
    ("截图漏翻的侧栏、缓存文案、对比度和系统命令说明完整显示中文", CheckMissingUi),
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
    const string termsCode = "const A={createElement:(tag,props,...children)=>({children})};console.log(JSON.stringify(A.createElement('div',null,'Alt text','Read URLs','Close all windows')));";
    var termTexts = JavaScriptUi.Extract(termsCode, "web/main.js").SelectMany(g => g.Texts).ToList();
    var labels = new Dictionary<string, string> { ["Alt text"] = "替代文本", ["Read URLs"] = "读取 URL", ["Close all windows"] = "关闭所有窗口" };
    var termMap = termTexts.ToDictionary(t => t.Key, t => labels[t.Source]);
    using var termsResult = JsonDocument.Parse(Node(JavaScriptUi.Apply(termsCode, termTexts, termMap)));
    Assert(termsResult.RootElement.GetProperty("children").EnumerateArray().Select(t => t.GetString()).SequenceEqual(labels.Values),
        "普通词义或英文复数被误当成品牌、快捷键而阻断正常汉化。");
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
    var expanded = ExpandedWebBundle.DirectoryPath(fixture.App, prepared.Receipt.BundleHash!);
    using (var bundle = EmbeddedWebBundle.Open(fixture.App.Server))
        foreach (var name in bundle.Files.Where(name => name.Contains('/')))
            Assert(File.ReadAllBytes(Path.Combine(expanded, name)).SequenceEqual(bundle.Read(name)), "安装后子目录资源与原件不同：" + name);
    using (var asar = new AsarArchive(fixture.App.Asar))
        Assert(asar.ReadText("dist/menu.js").Contains("复制", StringComparison.Ordinal), "桌面菜单未写入。");
    Assert(PatchInstaller.FindApplied(fixture.App, fixture.Data) is not null, "应用状态未确认。");
    var hash = AsarArchive.FileHash(fixture.App.Asar); PatchInstaller.Apply(fixture.Prepare());
    Assert(AsarArchive.FileHash(fixture.App.Asar) == hash, "重复汉化叠加了补丁。");
    File.WriteAllText(Path.Combine(expanded, "symbols-icons/icons/files/code.svg"), "corrupt icon");
    PatchInstaller.Apply(fixture.Prepare());
    using (var bundle = EmbeddedWebBundle.Open(fixture.App.Server))
        Assert(File.ReadAllBytes(Path.Combine(expanded, "symbols-icons/icons/files/code.svg"))
            .SequenceEqual(bundle.Read("symbols-icons/icons/files/code.svg")), "重新应用没有修复损坏的展开资源。");
    var updatedPackage = new TranslationPackage(fixture.Package.Entries.Select(entry => entry with { Translation = "拷贝" }).ToList());
    var updated = PatchBuilder.Prepare(fixture.App, updatedPackage, Path.Combine(fixture.Root, "updated-pack"), fixture.Data);
    PatchInstaller.Apply(updated);
    Assert(updated.Receipt.BundleHash != prepared.Receipt.BundleHash && !Directory.Exists(expanded) &&
        Directory.Exists(ExpandedWebBundle.DirectoryPath(fixture.App, updated.Receipt.BundleHash!)), "更新译文后未切换资源目录或清理旧资源。");
    Assert(AsarArchive.FileHash(fixture.App.Server) == originalServer, "修改了本机服务二进制。");
    File.AppendAllText(fixture.App.Server, "upstream server update");
    var updatedServer = AsarArchive.FileHash(fixture.App.Server);
    var refreshed = fixture.Prepare(); PatchInstaller.Apply(refreshed);
    Assert(refreshed.Receipt.ServerHash == updatedServer && refreshed.Receipt.ServerHash != prepared.Receipt.ServerHash,
        "本机服务单独更新后没有重新绑定中文资源。");
    PatchInstaller.Restore(fixture.App, fixture.Data);
    Assert(AsarArchive.FileHash(fixture.App.Asar) == originalAsar, "恢复没有还原原始资源。");
    Assert(!File.Exists(Path.Combine(fixture.Root, NativeBundleAdapter.ResourceDirectory, "web.zip")), "恢复后残留活动资源包。");
    Assert(!Directory.Exists(expanded), "恢复后残留展开的资源目录。");
    Assert(AsarArchive.FileHash(fixture.App.Server) == updatedServer, "恢复覆盖了更新后的本机服务。");
    using var changedLoader = new Fixture("function upstream(){return ['--new-launch-format'];}");
    var partial = changedLoader.Prepare(); PatchInstaller.Apply(partial);
    using var partialAsar = new AsarArchive(changedLoader.App.Asar);
    Assert(partial.Receipt.BundleHash is null && partial.Receipt.Notices.Count > 0 &&
        partialAsar.ReadText("dist/menu.js").Contains("复制", StringComparison.Ordinal) &&
        partialAsar.ReadText("dist/languageServer.js").Contains("--new-launch-format", StringComparison.Ordinal),
        "新版本启动结构变化时，没有保留原版前端并应用可用的桌面译文。");
    using var unsafeBundle = new Fixture(resourcePath: "../outside.bin");
    var safePartial = unsafeBundle.Prepare(); PatchInstaller.Apply(safePartial);
    using var safeAsar = new AsarArchive(unsafeBundle.App.Asar);
    Assert(safePartial.Receipt.BundleHash is null && safePartial.Receipt.Notices.Count > 0 &&
        safeAsar.ReadText("dist/menu.js").Contains("复制", StringComparison.Ordinal) &&
        !safeAsar.ReadText("dist/languageServer.js").Contains(NativeBundleAdapter.HookMarker, StringComparison.Ordinal),
        "无法安全展开资源时，没有保留原生加载方式并继续应用安全的菜单译文。");
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
      return UI.createElement('section',{title:UI.createElement('span',null,payload.displayName),description:payload.description},
        payload.buckets.map(item=>UI.createElement(Quota,{label:item.displayName,remainingFraction:item.fraction,refreshText:item.description,disabled:false})));
    }
    const data=JSON.parse('{"displayName":"Gemini Models","buckets":[{"displayName":"Weekly Limit Remaining","fraction":1},{"displayName":"Five Hour Limit Remaining","fraction":0.25},{"displayName":"Future Model $& Weekly Limit","fraction":0.92,"description":"You have used some of your weekly limit, it will fully refresh in 6 days, 19 hours."},{"displayName":"Experimental Rolling Limit","fraction":0.42,"description":"A new service phrase."}]}');
    let calls=0;Object.defineProperty(data,'displayName',{enumerable:true,get(){calls++;return 'Gemini Models';}});
    data.description='Models within this group: Claude Opus, Claude Sonnet, GPT-OSS, Future $&';
    const view=Group({group:data});const evaluated=calls;
    function Plan(state){const text=state?.userTier?.upgradeSubscriptionText||'';return UI.createElement('row',{description:text});}
    const account=JSON.parse('{"userTier":{"upgradeSubscriptionText":"You can upgrade to a Google AI Ultra plan to receive higher rate limits."}}');
    var editable='Weekly Limit Remaining';function edit(value){editable=value;}
    function Reused({label:caption,remainingFraction:ratio,refreshText:hint,disabled:off}){caption=editable;return UI.createElement('span',null,caption);}
    const outside=UI.createElement('span',null,editable),node={title:'A node'};
    const unchanged=Quota({label:null,refreshText:node});
    const singular=Quota({label:'Gemini 4 Argon Weekly Limit',refreshText:'You have used some of your weekly limit, it will fully refresh in 1 day, 2 hours and 3 minutes.'});
    const unknown=Quota({label:'Gemini 4 Argon',refreshText:'You have used some of your weekly limit, it will fully refresh in tomorrow.'});
    console.log(JSON.stringify({title:view.props.title.children[0],tooltip:view.props.description,rows:view.children[0].map(row=>({label:row.props.label.children[0],description:row.props.description,fraction:row.props.ratio})),
      evaluated,data,account,plan:Plan(account).props.description,outside:outside.children[0],reused:Reused({}).children[0],nodeUnchanged:unchanged.props.description===node,singular:singular.props.description,unknown:unknown.props}));
    """;
    var translations = new Dictionary<string, string>
    {
        ["Gemini Models"] = "Gemini 模型", ["Claude and GPT models"] = "Claude 和 GPT 模型",
        ["Models within this group: ⟪AGC0⟫"] = "本组包含的模型：⟪AGC0⟫",
        ["Weekly Limit Remaining"] = "本周剩余额度", ["Five Hour Limit Remaining"] = "5 小时剩余额度", ["⟪AGC0⟫ Weekly Limit"] = "⟪AGC0⟫ 每周额度",
        ["You have used some of your weekly limit, it will fully refresh in ⟪AGC0⟫."] = "你已使用部分每周额度，将在 ⟪AGC0⟫后完全恢复。",
        ["You can upgrade to a ⟪AGC0⟫ plan to receive higher rate limits."] = "升级至 ⟪AGC0⟫ 套餐可获得更高的使用额度。",
        ["You can upgrade to a higher ⟪AGC0⟫ plan to receive the highest rate limits."] = "升级至更高等级的 ⟪AGC0⟫ 套餐可获得最高的使用额度。",
        ["⟪AGC0⟫ day"] = "⟪AGC0⟫ 天", ["⟪AGC0⟫ days"] = "⟪AGC0⟫ 天", ["⟪AGC0⟫ hour"] = "⟪AGC0⟫ 小时", ["⟪AGC0⟫ hours"] = "⟪AGC0⟫ 小时",
        ["⟪AGC0⟫ minute"] = "⟪AGC0⟫ 分钟", ["⟪AGC0⟫ minutes"] = "⟪AGC0⟫ 分钟", ["⟪AGC0⟫ second"] = "⟪AGC0⟫ 秒", ["⟪AGC0⟫ seconds"] = "⟪AGC0⟫ 秒"
    };
    var texts = JavaScriptUi.Extract(code, "web/future-build.js").SelectMany(g => g.Texts).ToList();
    var map = texts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => translations[t.Source]);
    using var result = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var value = result.RootElement; var rows = value.GetProperty("rows");
    Assert(value.GetProperty("title").GetString() == "Gemini 模型" && rows[0].GetProperty("label").GetString() == "本周剩余额度" && rows[1].GetProperty("label").GetString() == "5 小时剩余额度", "服务下发的配额显示名称没有汉化。");
    Assert(value.GetProperty("tooltip").GetString() == "本组包含的模型：Claude Opus, Claude Sonnet, GPT-OSS, Future $&" && value.GetProperty("data").GetProperty("description").GetString()!.StartsWith("Models within this group:"), "悬浮说明未汉化，或改动了模型列表及原始说明。");
    Assert(rows[2].GetProperty("label").GetString() == "Future Model $& 每周额度" && rows[2].GetProperty("description").GetString() == "你已使用部分每周额度，将在 6 天 19 小时后完全恢复。", "型号内容或倒计时单位处理错误。");
    Assert(value.GetProperty("singular").GetString() == "你已使用部分每周额度，将在 1 天 2 小时 3 分钟后完全恢复。" && value.GetProperty("plan").GetString() == "升级至 Google AI Ultra 套餐可获得更高的使用额度。", "单复数时长或套餐名称处理错误。");
    Assert(value.GetProperty("evaluated").GetInt32() == 1 && value.GetProperty("data").GetProperty("displayName").GetString() == "Gemini Models" && rows[2].GetProperty("fraction").GetDouble() == 0.92 && value.GetProperty("account").GetProperty("userTier").GetProperty("upgradeSubscriptionText").GetString()!.StartsWith("You can upgrade"), "显示汉化改变了接口数据、求值次数或配额数值。");
    Assert(value.GetProperty("outside").GetString() == "Weekly Limit Remaining" && value.GetProperty("reused").GetString() == "Weekly Limit Remaining" && value.GetProperty("nodeUnchanged").GetBoolean() && rows[3].GetProperty("description").GetString() == "A new service phrase." && value.GetProperty("unknown").GetProperty("label").GetProperty("children")[0].GetString() == "Gemini 4 Argon" && value.GetProperty("unknown").GetProperty("description").GetString()!.EndsWith("tomorrow."), "误改用户内容、重新赋值的参数、模型名、非字符串节点或未知文案。");
}
static void CheckProjectDisplay()
{
    const string code = """
    const UI={createElement:(tag,props,...children)=>({props,children})};let events=0,order=[];
    const handlers={create:()=>++events};const create=handlers.create;
    const menu=[{label:'Create Project',id:'createProject',onClick:create},{label:'New Project',id:'newProject',onClick:handlers.create},{label:'Quick Start',id:'quickStart',onSelect:handlers.create}];
    function selector(outside,project){let caption=false;caption=caption?'Select Workspace':'Select Project';caption=outside?'No Project':project?.name??caption;const noun=outside?'projects':'workspaces';return UI.createElement('section',null,UI.createElement('button',{title:`Select project, current: ${caption}`},caption),UI.createElement('div',null,'No ',noun,' found'));}
    function unsafe(outside,project){let caption;caption=outside?'No Project':project?.name??'';const route=caption;return {route,compare:caption==='No Project',view:UI.createElement('span',null,caption)};}
    const userProject={name:'No Project',id:'user-project'};
    const none=selector(true),named=selector(false,userProject),defaultView=selector(false),raw=unsafe(true);
    function noun(){order.push(1);return 'projects';}function suffix(){order.push(2);return ' found';}
    const empty=UI.createElement('div',null,'No ',noun(),suffix());
    let effects=0;function count(){effects++;return 2;}const modes=UI.createElement('span',null,`${count()} active mode${++effects>1?'s':''}`);
    const terms={lowercase:'project'},resources=true;const settings=UI.createElement('span',null,`Manage ${terms.lowercase} ${resources?'resources':'folders'}, agent settings, and permissions.`);
    for(const item of menu)(item.onClick??item.onSelect)();
    console.log(JSON.stringify({menu,events,order,none,named,defaultView,userProject,raw,empty,modes,effects,settings,terms}));
    """;
    var translations = new Dictionary<string, string>
    {
        ["Create Project"] = "新建项目", ["New Project"] = "新建项目", ["Quick Start"] = "快速开始", ["No Project"] = "无项目",
        ["Select Project"] = "选择项目", ["Select Workspace"] = "选择工作区", ["Select project, current: ⟪AGC0⟫"] = "选择项目，当前：⟪AGC0⟫",
        ["No projects found"] = "未找到项目", ["No workspaces found"] = "未找到工作区",
        ["⟪AGC0⟫ active mode⟪AGC1⟫"] = "⟪AGC0⟫ 个启用模式⟪AGC1⟫",
        ["project"] = "项目", ["resources"] = "资源", ["folders"] = "文件夹",
        ["Manage ⟪AGC0⟫ ⟪AGC1⟫, agent settings, and permissions."] = "管理⟪AGC0⟫的⟪AGC1⟫、智能体设置和权限。"
    };
    var texts = JavaScriptUi.Extract(code, "web/project-selector.js").SelectMany(g => g.Texts).ToList();
    var map = texts.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => translations[t.Source]);
    using var result = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var value = result.RootElement;
    Assert(value.GetProperty("none").GetProperty("children")[0].GetProperty("children")[0].GetString() == "无项目" && value.GetProperty("defaultView").GetProperty("children")[0].GetProperty("children")[0].GetString() == "选择项目", "包含动态项目名的默认标题未汉化。");
    Assert(value.GetProperty("named").GetProperty("children")[0].GetProperty("children")[0].GetString() == "No Project" && value.GetProperty("userProject").GetProperty("name").GetString() == "No Project" && value.GetProperty("raw").GetProperty("route").GetString() == "No Project" && value.GetProperty("raw").GetProperty("compare").GetBoolean(), "用户项目名或业务比较被默认文案汉化影响。");
    Assert(value.GetProperty("none").GetProperty("children")[1].GetProperty("children")[0].GetString() == "未找到项目" && value.GetProperty("empty").GetProperty("children")[0].GetString() == "未找到项目" && value.GetProperty("order").EnumerateArray().Select(e => e.GetInt32()).SequenceEqual([1, 2]), "空状态出现片段残留，或改变了原有求值顺序和次数。");
    Assert(value.GetProperty("menu")[0].GetProperty("label").GetString() == "新建项目" && value.GetProperty("menu")[2].GetProperty("label").GetString() == "快速开始" && value.GetProperty("menu")[2].GetProperty("id").GetString() == "quickStart" && value.GetProperty("events").GetInt32() == 3, "菜单函数引用没有汉化，或改变了内部 ID 和操作事件。");
    Assert(value.GetProperty("modes").GetProperty("children")[0].GetString() == "2 个启用模式" && value.GetProperty("effects").GetInt32() == 2 && value.GetProperty("settings").GetProperty("children")[0].GetString() == "管理项目的资源、智能体设置和权限。" && value.GetProperty("terms").GetProperty("lowercase").GetString() == "project", "模板中残留英文单复数词尾或固定名词，或修改了求值及原始术语数据。");
}
static void CheckSettingOptions()
{
    const string code = """
    const UI={createElement:(tag,props,...children)=>({props,children}),useMemo:f=>f()};
    const presets={windows:{normal:{displayName:'Default',shortDescription:'Requires review.',longDescription:'All terminal commands require review.'},machine:{displayName:'Full machine',shortDescription:'Can access all files.',longDescription:'Read and write any file.'}}};
    let resolverCalls=0,descriptionCalls=0,tooltipCalls=0,selected='normal';const externalName='Server added choice';
    function Dropdown({value,options,displayResolver,onSelect,resolveOptionToDescription,getOptionTooltip}){
      const mapping=new Map(options.map(key=>[displayResolver(key),key]));
      return UI.createElement('section',{onValueChange:key=>onSelect(mapping.get(key))},UI.createElement('span',null,value),options.map(key=>{
        const label=displayResolver(key);let item=key;item=resolveOptionToDescription?.(key);
        return UI.createElement('option',{value:label,subtitle:item,tooltipText:getOptionTooltip?.(key)},key==='normal'?`${label} (disabled by admin)`:label);
      }));
    }
    const options={value:'Full machine',options:['normal','machine','future'],displayResolver:key=>{resolverCalls++;return presets.windows[key]?.displayName??externalName;},
      resolveOptionToDescription:key=>{descriptionCalls++;return presets.windows[key]?.shortDescription;},getOptionTooltip:key=>{tooltipCalls++;return key==='normal'?'Disabled by organization policy':undefined;},onSelect:key=>{selected=key;}};
    const view=Dropdown(options);view.props.onValueChange('Full machine');
    const detail=UI.useMemo(()=>presets.windows[selected]?.longDescription??'');const explanation=UI.createElement('span',null,detail);
    console.log(JSON.stringify({view,explanation,selected,options,presets,resolverCalls,descriptionCalls,tooltipCalls}));
    """;
    var translations = new Dictionary<string, string>
    {
        ["Default"] = "默认", ["Full machine"] = "整机访问", ["Requires review."] = "需要审核。", ["Can access all files."] = "可访问所有文件。",
        ["All terminal commands require review."] = "所有终端命令都需要审核。", ["Read and write any file."] = "可读写任何文件。", ["Disabled by organization policy"] = "组织策略已禁用",
        ["⟪AGC0⟫ (disabled by admin)"] = "⟪AGC0⟫（管理员已禁用）"
    };
    var texts = JavaScriptUi.Extract(code, "web/settings.js").SelectMany(g => g.Texts).ToList();
    var map = texts.Where(t => translations.ContainsKey(t.Source)).DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => translations[t.Source]);
    using var result = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var value = result.RootElement;
    var view = value.GetProperty("view"); var choices = view.GetProperty("children")[1];
    Assert(view.GetProperty("children")[0].GetProperty("children")[0].GetString() == "整机访问" && choices[0].GetProperty("children")[0].GetString() == "默认（管理员已禁用）" && choices[1].GetProperty("props").GetProperty("subtitle").GetString() == "可访问所有文件。", "已选值、选项名称或说明未显示中文。");
    Assert(choices[1].GetProperty("props").GetProperty("value").GetString() == "Full machine" && value.GetProperty("selected").GetString() == "machine" && value.GetProperty("options").GetProperty("value").GetString() == "Full machine" && value.GetProperty("presets").GetProperty("windows").GetProperty("normal").GetProperty("displayName").GetString() == "Default", "汉化改变了选项映射、权限回调或原始配置。");
    Assert(value.GetProperty("explanation").GetProperty("children")[0].GetString() == "可读写任何文件。" && choices[0].GetProperty("props").GetProperty("tooltipText").GetString() == "组织策略已禁用" && choices[2].GetProperty("children")[0].GetString() == "Server added choice", "长说明或禁用原因未汉化，或错误改动未知新选项。");
    Assert(value.GetProperty("resolverCalls").GetInt32() == 6 && value.GetProperty("descriptionCalls").GetInt32() == 3 && value.GetProperty("tooltipCalls").GetInt32() == 3, "显示转换改变了解析函数的调用次数。");
}
static void CheckRecovery()
{
    using var fixture = new Fixture(); var original = AsarArchive.FileHash(fixture.App.Asar);
    var originalBytes = File.ReadAllBytes(fixture.App.Asar);
    var prepared = fixture.Prepare();
    using (var locked = new FileStream(fixture.App.Executable, FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => PatchInstaller.Apply(prepared));
    Assert(AsarArchive.FileHash(fixture.App.Asar) == original, "第二个文件失败后未恢复第一个文件。");
    Assert(!Directory.Exists(ExpandedWebBundle.DirectoryPath(fixture.App, prepared.Receipt.BundleHash!)), "失败回滚后残留本次展开的资源。");
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
static void CheckMissingUi()
{
    const string code = """
    const UI={useMemo:f=>f(),createElement:(tag,props,...children)=>({props,children})};
    const screens=[{screen:'General'},{screen:'App',label:'Application'}];
    const names=new Map([...screens.filter(s=>s.label!==undefined).map(s=>[s.screen,s.label]),['Feedback','Give Feedback']]);
    const sections=new Map([['General',{sections:[{title:'Execution',settings:['queueStrategy']}]}]]);
    const headers=sections.get('General').sections.map(section=>UI.createElement('heading',{title:section.title},section.settings.length));
    let calls=0;function Environment(selected){const label=UI.useMemo(()=>{calls++;switch(selected.type){case 'local':return 'Local';default:return selected.name;}},[selected]);
      const description=selected.node?UI.createElement('span',null,''): 'Controls the actions the agent can take.';
      return UI.createElement('div',{description},label);}
    const resolver=n=>{switch(n){case 2:return 'Strong';default:return 'Default'}};
    function Toggle({options,onSelect,resolveOptionToLabel}){const rows=UI.useMemo(()=>options.map(value=>{const label=resolveOptionToLabel?resolveOptionToLabel(value):String(value);return{value:String(value),label:label};}),[options,resolveOptionToLabel]);return UI.createElement('button',{onClick:()=>onSelect(options.find(value=>String(value)===rows[0].value))},rows[0].label);}
    const config={settingKey:'contrast',label:'Contrast',options:[2,1],resolveOptionToString:resolver};let selected=0;
    const contrast=Toggle({options:config.options,onSelect:value=>selected=value,resolveOptionToLabel:config.resolveOptionToString});contrast.props.onClick();
    const schema={ContextScopeItemSchema:{}};const create=(type,value)=>value;
    class Item{constructor(scope,extra,unused,description){this.scope=scope;this.description=description;}}
    function Command(entry){return new Item(create(schema.ContextScopeItemSchema,{scopeItem:{case:'slashCommand',value:{info:entry.info}}}),[],undefined,entry.description);}
    const system={info:{name:'goal',type:1},description:'Run until the specified goal is completely finished.'};
    const custom={info:{name:'my-goal',type:2},description:system.description};
    const builtin=Command(system),user=Command(custom);
    const local=Environment({type:'local'}),named=Environment({type:'custom',name:'Local'});
    const formatEnvironment=(environment,available=[])=>{switch(environment.type){case 'sot':return 'Local';case 'copy':return 'New Worktree';case 'environment':return environment.name;default:return 'Unknown Environment';}};
    function EnvironmentSelector(environment){return UI.createElement('span',null,formatEnvironment(environment,[]));}
    const builtinEnvironment=EnvironmentSelector({type:'sot'}),customEnvironment=EnvironmentSelector({type:'environment',name:'Local'});
    const worktree=UI.createElement('selector',{disabledWorktreeSubtitle:'Worktrees are available for Git repositories'});
    function Plan(state){return UI.createElement('span',null,state.userTier.upgradeSubscriptionText);}
    const account={userTier:{upgradeSubscriptionText:'You can upgrade to a higher Google AI Ultra plan to receive the highest rate limits.'}};
    console.log(JSON.stringify({screens,names:[...names],sections:[...sections],headers,sectionLookup:!!sections.get('General').sections.find(section=>section.title==='Execution'),local,named,calls,builtinEnvironment,customEnvironment,rawEnvironment:formatEnvironment({type:'sot'}),contrast,selected,rawResolver:resolver(2),builtin,user,system,worktree,account,plan:Plan(account)}));
    """;
    var translations = new Dictionary<string, string>
    {
        ["General"] = "常规", ["Application"] = "应用", ["Give Feedback"] = "反馈意见", ["Execution"] = "执行",
        ["Local"] = "本地", ["Controls the actions the agent can take."] = "控制智能体可以执行的操作。",
        ["Contrast"] = "对比度", ["Strong"] = "增强", ["Default"] = "默认",
        ["Run until the specified goal is completely finished."] = "持续执行，直到指定目标全部完成。",
        ["Worktrees are available for Git repositories"] = "Git 仓库支持使用工作树",
        ["You can upgrade to a higher ⟪AGC0⟫ plan to receive the highest rate limits."] = "升级至更高等级的 ⟪AGC0⟫ 套餐可获得最高使用额度。"
    };
    var texts = JavaScriptUi.Extract(code, "web/main.js").SelectMany(g => g.Texts).ToList();
    var map = texts.Where(t => translations.ContainsKey(t.Source)).DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => translations[t.Source]);
    using var parsed = JsonDocument.Parse(Node(JavaScriptUi.Apply(code, texts, map))); var result = parsed.RootElement;
    Assert(result.GetProperty("screens")[0].GetProperty("screen").GetString() == "General" &&
        result.GetProperty("names")[0][1].GetString() == "常规" && result.GetProperty("names")[2][1].GetString() == "反馈意见", "展开映射后的侧栏漏翻，或路由身份被改动。");
    Assert(result.GetProperty("sections")[0][0].GetString() == "General" && result.GetProperty("sections")[0][1].GetProperty("sections")[0].GetProperty("title").GetString() == "Execution" &&
        result.GetProperty("headers")[0].GetProperty("props").GetProperty("title").GetString() == "执行" && result.GetProperty("sectionLookup").GetBoolean(), "设置分组标题未汉化，或分组查找身份被修改。");
    Assert(result.GetProperty("local").GetProperty("children")[0].GetString() == "本地" &&
        result.GetProperty("local").GetProperty("props").GetProperty("description").GetString() == "控制智能体可以执行的操作。" &&
        result.GetProperty("named").GetProperty("children")[0].GetString() == "Local" && result.GetProperty("calls").GetInt32() == 2, "缓存分支文案漏翻，或用户环境名称、求值次数被改动。");
    Assert(result.GetProperty("contrast").GetProperty("children")[0].GetString() == "增强" && result.GetProperty("selected").GetInt32() == 2 && result.GetProperty("rawResolver").GetString() == "Strong", "对比度选项漏翻，或内部选项映射被改动。");
    Assert(result.GetProperty("builtinEnvironment").GetProperty("children")[0].GetString() == "本地" &&
        result.GetProperty("customEnvironment").GetProperty("children")[0].GetString() == "Local" && result.GetProperty("rawEnvironment").GetString() == "Local", "环境格式化路径漏翻，或自定义名称、原始格式化函数被修改。");
    Assert(result.GetProperty("builtin").GetProperty("description").GetString() == translations["Run until the specified goal is completely finished."] &&
        result.GetProperty("user").GetProperty("description").GetString() == result.GetProperty("system").GetProperty("description").GetString() &&
        result.GetProperty("builtin").GetProperty("scope").GetProperty("scopeItem").GetProperty("value").GetProperty("info").GetProperty("name").GetString() == "goal", "系统命令说明未汉化，或自定义技能内容、命令身份被改动。");
    Assert(result.GetProperty("worktree").GetProperty("props").GetProperty("disabledWorktreeSubtitle").GetString() == "Git 仓库支持使用工作树" &&
        result.GetProperty("plan").GetProperty("children")[0].GetString() == "升级至更高等级的 Google AI Ultra 套餐可获得最高使用额度。" &&
        result.GetProperty("account").GetProperty("userTier").GetProperty("upgradeSubscriptionText").GetString()!.StartsWith("You can upgrade"), "工作树提示或新套餐说明未汉化，或接口原始数据被改动。");
}
static void CheckNativeHook()
{
    using var fixture = new Fixture(); var prepared = fixture.Prepare(); PatchInstaller.Apply(prepared);
    var expanded = ExpandedWebBundle.DirectoryPath(fixture.App, prepared.Receipt.BundleHash!);
    var hook = Path.Combine(fixture.Root, "hook.cjs");
    using (var asar = new AsarArchive(fixture.App.Asar)) File.WriteAllBytes(hook, asar.Read(NativeBundleAdapter.HookFile));
    var root = JavaScriptUi.Quote(Path.Combine(fixture.Root, "resources")); var module = JavaScriptUi.Quote(hook);
    var output = Node($$"""
    Object.defineProperty(process,'resourcesPath',{value:{{root}}});
    const m=require({{module}}),fs=require('fs'),path=require('path');
    const invoke=wsl=>{const args=[];m.addBundle(args,wsl);return args;};
    const valid=invoke(false),wsl=invoke(true);
    const icon=path.join(valid[1],'symbols-icons/icons/files/code.svg'),original=fs.readFileSync(icon);
    fs.writeFileSync(icon,'corrupt icon');const corrupt=invoke(false);
    fs.unlinkSync(icon);const missing=invoke(false);
    fs.writeFileSync(icon,original);
    const binary=path.join({{root}},'bin','language_server.exe'),stat=fs.statSync(binary),bytes=fs.readFileSync(binary);
    bytes[0]^=1;fs.writeFileSync(binary,bytes);fs.utimesSync(binary,stat.atime,stat.mtime);
    console.log(JSON.stringify([valid,wsl,corrupt,missing,invoke(false)]));
    """);
    using var parsed = JsonDocument.Parse(output);
    Assert(parsed.RootElement[0][0].GetString() == "--web_bundle_path" && parsed.RootElement[0][1].GetString() == expanded &&
        parsed.RootElement.EnumerateArray().Skip(1).All(a => a.GetArrayLength() == 0),
        "原生入口未使用完整资源目录，或资源损坏、缺失、服务变化及 WSL 的回退失效。");
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetFullPath("work/checks"), Guid.NewGuid().ToString("N"));
    public string Data => Path.Combine(Root, "state");
    public Installation App { get; }
    public TranslationPackage Package { get; }
    public Fixture(string loader = "function start(options){const {wsl}=options;const args=['--standalone','--subclient_type','hub','--csrf_token'];return args;}",
        string resourcePath = "unchanged.bin")
    {
        Directory.CreateDirectory(Path.Combine(Root, "resources", "bin"));
        File.Copy(Environment.ProcessPath!, Path.Combine(Root, "Antigravity.exe"));
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new Dictionary<string, string> { ["index.html"] = "<script src=\"/main.js\"></script>",
                         ["main.js"] = "A.createElement('button',null,'Copy');", [resourcePath] = "untouched resource",
                         ["symbols-icons/icons/files/code.svg"] = "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0h16v16\"/></svg>",
                         ["workers/example.mjs"] = "export const workerName='fixture';" })
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
