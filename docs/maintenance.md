# 跟随 Antigravity App 维护

维护目标是长期保留可用中文界面。上游发布新版本并不自动意味着本项目需要更新；文案、适配和使用效果才是更新依据。

## 先检查实际安装

在仓库根目录用 PowerShell 7 执行：

```powershell
./scripts/maintain.ps1 -Mode Check
# 也可指定另一份安装或解压后的 App：
./scripts/maintain.ps1 -Mode Check -AppPath 'D:\apps\Antigravity'
```

Check 只分析资源并将结果写入忽略目录 `.local`。`NeedsUpdate=false` 时直接结束：不用 ATT、不改翻译资产、不为版本号创建提交。新增语境中的文字若能无歧义复用已有译文，也不要求重新发布。

出现结构提示时，先检查新的前端封包、显示调用和本机服务启动参数，修正适配器；不要修改版本号冒充适配。安装工具仍允许应用其他安全匹配部分。

## 准备本项目的 ATT

使用完整 ATT 发行包，或已构建的 ATT 源码仓库。当前使用 ATT 1.3.6。

```powershell
./scripts/setup-att.ps1 -AttRoot 'D:\att' -ConfigurationPath 'D:\private\att\config.toml'
```

配置复制到 `.local/att/config.toml`。后续重新准备运行目录时默认保留已有配置，源码仓库的可执行文件必须与 Cargo.toml 版本一致。不要把密钥贴进文档、命令输出或 Git。脚本在 ATT 基础 Prompt 后附加 `translation/style-guide.md`。

## 增量翻译和人工复核

```powershell
./scripts/maintain.ps1 -Mode Update -Profile opencode
```

Profile 使用本机 ATT 已配置的名称，不在本项目固定模型。后续可省略 `-Profile`，复用 ATT 上次成功的选择。

Update 会：提取新语境、生成保护规则、初始化或继续 `AntigravityChinese` Generic 项目、将发布包中适用的译文和明确保留项回填为 Manual，再翻译剩余条目，导出 QA 材料并导入写回结果。已有可复用译文不会因构建文件改名、位置移动或新版本号而整库重译。

审校 `.local/translations.jsonl`、`.local/pending.toml`、`.local/rejected.toml`，重点核对片段拼接、按钮实际含义、名称、动态占位符和语义漏译。拒绝项不会自动反复请求。少量补译或修订使用 ATT Manual：

```powershell
$att = '.local/att/att.exe'
& $att generic manual apply --name AntigravityChinese .local/reviewed.toml
& $att generic translation export --name AntigravityChinese .local/translations.jsonl
& $att generic write-back --name AntigravityChinese
dotnet run --project tools/AntigravityChinese.Maintainer -c Release -- import-att --write-back .local/att/projects/generic/AntigravityChinese/write_back
./scripts/maintain.ps1 -Mode Check
```

`reviewed.toml` 应从官方 `manual export` 取得 ID 和原文，仅填写译文。Manual 中 `source` 和 `translation` 为字符串数组；动态标记 `⟪AGC0⟫` 等必须保留原顺序。明确属于名称而保留整项原文的条目写入 `translation/keep-original.json`，不要借此隐藏普通文案漏译。

静态 QA 可使用 ATT 随包的 `skills/translate-with-att/scripts/translation_qa.py scan`，传入 `--translations`、`--generic-input translation/input`、`--terminology translation/terminology.toml` 和实际 `--write-back`。按 ATT 自身文档配置 Python 环境。QA 中的品牌、路径和代码残留提示需要人工判断，不要求把所有英文都翻掉。

## 验证和发布

```powershell
dotnet run --project tests/AntigravityChinese.Checks -c Release
dotnet run --project tools/AntigravityChinese.Maintainer -c Release -- verify-copy --work .local/verification-new
dotnet run --project tools/AntigravityChinese.Maintainer -c Release -- apply --app .local/verification-new/app --work .local/verification-patch --data .local/verification-new/state
node scripts/verify-bundle.mjs .local/verification-new/app/resources/bin/language_server.exe .local/verification-patch/web.zip .local/native-check
```

`verify-copy` 要求空目录及未汉化的原版安装。它在副本所有模块加载之前隔离 home、userData 等文件目录，并关闭副本更新检查。正式补丁没有这些改动。App 仍可能复用系统级登录服务，不要将文件目录隔离视为账户隔离。启动副本时再传入隔离环境：

```powershell
$sandbox = (Resolve-Path .local/verification-new/profile).Path
$environment = @{USERPROFILE=$sandbox;HOME=$sandbox;APPDATA=$sandbox;LOCALAPPDATA=$sandbox;ANTIGRAVITY_VSCODE_HOST='1'}
Start-Process .local/verification-new/app/Antigravity.exe -Environment $environment -WindowStyle Normal
```

原生验证检查真实服务返回的中文脚本、缓存地址、样式和配置 RPC。随后观察启动界面、菜单、主要页面、重启后的显示，以及安装和恢复。不要主动登录、发送消息、修改账户或安全设置；这些验证另行取得授权。不要将含账户信息的截图放入仓库。

退出验证副本后恢复并核对原件：

```powershell
dotnet run --project tools/AntigravityChinese.Maintainer -c Release -- restore --app .local/verification-new/app --data .local/verification-new/state
./scripts/publish.ps1
```

只在译文或适配确有变化且相关验证完成后提交、发布。实际发布时更新 `Directory.Build.props` 中的工具版本；安装记录同时包含实现版本，纯适配更新也能重新应用。验证范围更新到 `docs/verification.md`；不要发布 `.local` 或原版安装文件。

源码构建由 `global.json` 固定 .NET SDK 10.0.401。普通开发使用默认 `packages.lock.json`，单文件发布由脚本指定 `packages.publish.lock.json`，避免发布添加的运行时依赖覆盖开发锁文件。更新依赖时分别还原两种配置，再用 README 中的命令验证；发布脚本始终使用锁定模式，不在打包时自动更新依赖。NuGet 参数见[官方锁文件说明](https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#lock-file-extensibility)。

## 文件与运行契约

| 文件或目录 | 用途 |
| --- | --- |
| `translation/catalog.json` | 稳定语境与显示槽映射；保留已见过的语境以复用旧版有效译文 |
| `translation/input/*.jsonl` | ATT Generic 的原文输入 |
| `translation/terminology.toml` | 通用术语定译 |
| `translation/style-guide.md` | 简体中文风格和专名要求 |
| `translation/placeholders.toml` | 从安装器同源规则生成的名称和动态表达式保护 |
| `packs/zh-CN.json` | 安装器内置的有效译文与明确保留项 |
| `.local/att/projects/generic/AntigravityChinese` | 本机 ATT 数据库、日志和 WriteBack |

安装时从当前原件生成资源，核对准备期间文件未变，再按文件替换并校验。`language_server.exe` 保持原始字节；中文 ZIP 与该服务的大小、时间戳和 SHA-256 关联。启动时用固定缓冲区分块核对服务和 ZIP 摘要，资源或服务发生变化时回退到原版界面。

需要同步修改 `Antigravity.exe` 中的 ASAR 头校验值，因此补丁后的 EXE 内容与官方签名原件不同；校验开关、登录逻辑和更新器没有修改。恢复使用已保存的原件字节。安装记录优先选最近一次匹配的操作，避免不同原件恰好生成相同补丁时恢复到更早版本。

提取器区分显示与身份：命令保留 `id` 和快捷键，设置保留策略值及 `screen` 路由；需要时为已识别的 `screen → label` 注册表补充显示名。显示变量采用有界的静态来源分析，写回时只转换显示值，不能把用户可编辑值当作界面常量。代码标签、语言组件及等宽预格式区域连同嵌套的高亮节点一起排除。

菜单的显示配置支持直接函数、函数变量和对象方法回调。局部变量同时承载默认标题与用户名称时，只有后续读取均用于显示、赋值路径明确且没有闭包写入，才翻译其中的固定文字分支；用户项目名仍来自原始数据，即使名称恰为 `No Project` 也不改变。DOM 相邻文本片段能静态确定完整句子时，整句进入 ATT，防止“没有 projects found”这类半句汉化；原表达式仍按原顺序各求值一次。模板内可确定的界面词也单独提取，已译模板中的英文复数词尾保留条件求值后省去。

下拉框可能用英文标签建立选项映射，不能直接翻译其内部值。设置选项适配按 `options`、`displayResolver`、`onSelect` 等组件参数及预设元数据确认来源，把静态可确定的标签、简短说明、详细说明和禁用原因送入 ATT；在最终显示节点使用精确匹配。原始解析结果、映射键、选项值和权限回调保持原样，即使两个中文标签相同也不会合并原有选项。无法识别的新选项和动态说明保留原文。

服务端下发的文字不一定存在于静态脚本中。收到截图反馈后先确认最终消费者；当前 `ServiceDisplay` 只登记已确认的套餐升级说明、配额分组和额度标签、模型列表悬浮说明、刷新说明及时长单位。语法树按配额组件参数和 `userTier.upgradeSubscriptionText` 来源识别显示槽，不依赖压缩后的函数名，也不转换接口对象。已知模板同样进入 ATT Generic 和发布包；模型列表和套餐名称作为原样捕获值，时长只转换单位。未知句式、未登记的单位和非字符串节点直接保持原值。静态 Check 能检查这些已登记模板及显示适配，无法发现服务端后来新增的所有句子；实际页面反馈仍是维护依据。
