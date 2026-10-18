# Antigravity App 汉化工具

Windows x64 的离线、一次性汉化工具，目标是独立的 **Antigravity App**。使用内置简体中文译文，支持自动检测安装、手动选择目录、备份和恢复。

当前以 Antigravity App **2.24.0** 为验证基线，内置 3,853 条译文。工具每次分析实际安装资源，**没有版本白名单**：已知且语境适用的文案显示中文，新增或无法确认的内容保留原文。官方更新覆盖汉化后，退出 App，再手动运行一次本工具。

## 使用

解压分发包，运行 `AntigravityChinese.exe`，无需安装 .NET、ATT 或配置 API 密钥。

1. 结束 App 中正在进行的任务，完全退出 Antigravity App。
2. 核对安装目录，点击“开始汉化”。受保护目录可能需要 Windows 管理员权限。
3. 完成后自动打开 App。切换页面和重启都会继续使用写入的中文资源。
4. 需要撤销时，退出 App，再点击“恢复原版”。

备份保存在 `%LOCALAPPDATA%\AntigravityChinese\backups`。恢复前核对当前文件，旧备份不会覆盖不匹配的新安装。工具没有常驻进程、计划任务或自动更新监控。

## 汉化范围与兼容方式

- 固定界面、设置、按钮、提示、菜单和对话框。
- 用户输入、模型生成内容、代码、终端输出、第三方网页保持原样。
- Docker、GitHub、token、品牌、模型、协议和没有通用中文译法的名称保留英文；设置、仓库、分支等通用用语正常翻译。
- 通过 JavaScript 语法树定位显示位置，不对整个安装目录做字符串替换。动态表达式保持数量、顺序和求值次数，内部值和业务比较保持原样。
- 主界面使用本机服务已有的 `--web_bundle_path` 入口；不修改 `language_server.exe`，不拦截请求，不使用 DOM 替换。
- 服务下发的配额标签、模型分组悬浮说明、套餐升级说明和刷新时长，在确认的显示字段中按已知模板汉化；型号、套餐名、额度数值及原始接口数据保持原样，未知句式不改动。
- 新版本某部分结构暂时无法识别时，保留该部分原版资源并显示提示，其他安全匹配的部分仍可汉化。完全没有可用译文时不写入。

目前主界面适配 Windows 本机服务。切换到 WSL 后端时保留原版主界面。部分固定提示（如 `Execution`、`Chat Settings`、部分主题名称），以及尚未登记的服务端文案、公告和引导内容仍保留英文。模型调用、远程连接、所有设置和上游自动更新尚未逐项实机验收；已验证的范围见 [验证记录](docs/verification.md)。

## 构建与发布

开发需要 Windows、.NET 10 SDK；行为检查另需 Node.js。普通使用者只需分发包。

```powershell
dotnet restore AntigravityChinese.slnx --locked-mode
dotnet build AntigravityChinese.slnx -c Release --no-restore
dotnet run --project tests/AntigravityChinese.Checks -c Release
pwsh -File scripts/publish.ps1
```

`release/` 中生成包含运行时的程序、使用说明、许可证、ZIP 和 SHA-256 清单。分发包不含 ATT、密钥、原版程序或账户数据。

## 跟随上游维护

[维护说明](docs/maintenance.md)提供可直接执行的流程。先检查本机新版本：

```powershell
pwsh -File scripts/maintain.ps1 -Mode Check
```

若现有译文和加载方式仍适用，不更新翻译资产，不请求模型，不为版本号单独提交或发布。只有发现新文案、译文问题或资源结构变化时才维护；使用 ATT Generic 持续项目保留已确认译文，只处理增量。

源码参考 Docker Desktop 汉化工具，保留其 [MIT 许可](licenses/DockerDesktopChinese-MIT.txt)。ASAR、Electron 校验处理和桌面工具结构已按本项目适配。代码和译文采用 [MIT License](LICENSE)，第三方许可证保留在 [licenses](licenses/) 中。本项目为社区作品。
