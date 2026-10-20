# Antigravity App 汉化工具

Windows x64 的离线、一次性汉化工具，目标是独立的 **Antigravity App**。使用内置简体中文译文，支持自动检测安装、手动选择目录、备份和恢复。

当前发布为 **v0.1.5 公开测试版**。从 [Releases](https://github.com/yexi-by/AntigravityChinese-OpenSource/releases) 下载 `AntigravityChinese-win-x64.zip`，解压后使用；旁边的 `SHA256SUMS.txt` 用于核对下载文件和包内程序的 SHA-256。已使用 v0.1.4 的用户请重新应用本版，修复文件图标及其他子目录资源加载失败的问题。

当前以 Antigravity App **2.24.0** 为验证基线，内置 3,853 条译文。工具每次分析实际安装资源，**没有版本白名单**：已知且语境适用的文案显示中文，新增或无法确认的内容保留原文。官方更新覆盖汉化后，退出 App，再手动运行一次本工具。

## 使用

解压分发包，运行 `AntigravityChinese.exe`，无需安装 .NET、ATT 或配置 API 密钥。

1. 结束 App 中正在进行的任务，完全退出 Antigravity App。
2. 核对安装目录，点击“开始汉化”。受保护目录可能需要 Windows 管理员权限。
3. 完成后自动打开 App。切换页面和重启都会继续使用写入的中文资源。
4. 需要撤销时，退出 App，再点击“恢复原版”。

备份保存在 `%LOCALAPPDATA%\AntigravityChinese\backups`。恢复前核对当前文件，旧备份不会覆盖不匹配的新安装。工具没有常驻进程、计划任务或自动更新监控。

本工具会修改 App 资源和 `Antigravity.exe` 内的 ASAR 校验值，因此汉化后的 EXE 与官方签名原件不同；恢复原版会还原保存的原件字节。本工具的分发程序目前未签名。

## 汉化范围与兼容方式

- 固定界面、设置、按钮、提示、菜单和对话框。
- 用户输入、模型生成内容、代码、终端输出、第三方网页保持原样。
- Docker、GitHub、token、品牌、模型、协议和没有通用中文译法的名称保留英文；设置、仓库、分支等通用用语正常翻译。
- 通过 JavaScript 语法树定位显示位置，不对整个安装目录做字符串替换。动态表达式保持数量、顺序和求值次数，内部值和业务比较保持原样。
- 主界面使用本机服务已有的 `--web_bundle_path` 目录入口，保留图标、图片和 worker 的原始路径。启动前校验服务、中文 ZIP 和展开的资源；不修改 `language_server.exe`，不拦截请求，不使用 DOM 替换。
- 服务下发的配额标签、模型分组悬浮说明、套餐升级说明和刷新时长，在确认的显示字段中按已知模板汉化；型号、套餐名、额度数值及原始接口数据保持原样，未知句式不改动。
- 新版本某部分结构暂时无法识别时，保留该部分原版资源并显示提示，其他安全匹配的部分仍可汉化。完全没有可用译文时不写入。

目前主界面适配 Windows 本机服务。切换到 WSL 后端时保留原版主界面。部分固定提示（如 `Execution`、`Chat Settings`、部分主题名称），以及尚未登记的服务端文案、公告和引导内容仍保留英文。模型调用、远程连接、所有设置和上游自动更新尚未逐项实机验收；已验证的范围见 [验证记录](docs/verification.md)。

## 构建与发布

开发需要 Windows、.NET SDK **10.0.401**（由 `global.json` 固定）和 PowerShell 7；行为检查另需 Node.js。普通使用者只需分发包。

```powershell
dotnet restore AntigravityChinese.slnx --locked-mode
dotnet build AntigravityChinese.slnx -c Release --no-restore
dotnet run --project tests/AntigravityChinese.Checks -c Release --no-restore
pwsh -File scripts/publish.ps1
```

`release/` 中生成包含运行时的程序、使用说明、许可证、ZIP 和 SHA-256 清单。分发包不含 ATT、密钥、原版程序或账户数据。

普通开发使用 `packages.lock.json`；发布脚本使用 `packages.publish.lock.json`，单独锁定 Windows x64 单文件发布的依赖。两种操作互不覆盖锁文件，均可在锁定模式下还原。

## 跟随上游维护

[维护说明](docs/maintenance.md)提供可直接执行的流程。先检查本机新版本：

```powershell
pwsh -File scripts/maintain.ps1 -Mode Check
```

若现有译文和加载方式仍适用，不更新翻译资产，不请求模型，不为版本号单独提交或发布。只有发现新文案、译文问题或资源结构变化时才维护；使用 ATT Generic 持续项目保留已确认译文，只处理增量。

反馈漏译或异常时，请在 [Issues](https://github.com/yexi-by/AntigravityChinese-OpenSource/issues) 附上 App 版本、工具版本、触发步骤和截图，并遮住邮箱、对话内容和其他个人信息。

源码参考 Docker Desktop 汉化工具，保留其 [MIT 许可](licenses/DockerDesktopChinese-MIT.txt)。ASAR、Electron 校验处理和桌面工具结构已按本项目适配。代码和译文采用 [MIT License](LICENSE)，第三方许可证保留在 [licenses](licenses/) 中。本项目为社区作品。
