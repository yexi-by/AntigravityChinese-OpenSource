# 维护

先检查实际安装：

```powershell
./scripts/maintain.ps1 -Mode Check
```

现有包适用时直接重新应用，不修改翻译资产。需要补译时沿用 `.local/att` 的现有项目和配置：

```powershell
./scripts/maintain.ps1 -Mode Update
```

`translation/style-guide.md` 是本项目的完整翻译提示词，准备 ATT 时会替换通用提示词及响应示例。模型、思考等级和并发只配置在 `.local/att/config.toml`。占位符规则及硬性校验仅保护动态变量；品牌与技术术语直接提供给模型，由审校确认保留，不以宽泛的词汇正则阻断正常译法。更换模型不会自动重译已有译文，全量重译需明确清除现有项目的译文状态，再调用 Translate；不要回填旧包。

集中审校后，通过 ATT Manual 修订，再写回并导入：

```powershell
.local/att/att.exe generic manual apply --name AntigravityChinese .local/reviewed.toml
.local/att/att.exe generic write-back --name AntigravityChinese
dotnet run --project tools/AntigravityChinese.Maintainer -c Release -- import-att --write-back .local/att/projects/generic/AntigravityChinese/write_back
```

源码构建与打包：

```powershell
dotnet restore AntigravityChinese.slnx --locked-mode
dotnet build AntigravityChinese.slnx -c Release --no-restore
dotnet run --project tests/AntigravityChinese.Checks -c Release --no-restore
./scripts/publish.ps1
```

发布前更新 `Directory.Build.props` 中的工具版本。分发包只包含工具、使用说明和许可证。
