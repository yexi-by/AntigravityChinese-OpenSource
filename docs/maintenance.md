# 维护

先检查实际安装：

```powershell
./scripts/maintain.ps1 -Mode Check
```

现有包适用时直接重新应用，不修改翻译资产。需要补译时沿用 `.local/att` 的现有项目和配置：

```powershell
./scripts/maintain.ps1 -Mode Update
```

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
