# 构建与发布

需要 Windows x64 和 .NET Framework 4.8 或更高版本，不依赖 NuGet 包。

```powershell
.\build.ps1
.\test.ps1
.\test-ui.ps1
.\test-updates.ps1
.\test-portable.ps1
.\check-public-files.ps1
```

构建输出为 `dist\LaTaleGarden.exe`。测试结果保存在忽略的 `test-results` 目录，不提交日志、实机报告或个人截图。

首次开发时运行 `git config core.hooksPath .githooks`，启用提交及推送检查。公开截图只使用示例目录与模拟状态。

发布时同步 `Program.cs`、`AssemblyInfo.cs` 和版本说明。先创建版本标签并上传经过检查的单个 EXE，再发布对应更新清单；Release 标题使用版本号，正文仅列版本变化。

```powershell
.\prepare-update.ps1 -NotesFile .\docs\releases\v1.1.5.md -Channel stable
.\sign-document.ps1 -InputFile .\updates\announcements.source.json -OutputFile .\updates\announcements.json
.\test-published-update.ps1 -Local
```

发布签名私钥只存放在发布者本机，不能提交到仓库或附在 Release 中。客户端内置公钥用于检查更新信息；已有客户端使用的信任公钥不要随意更换。

真实更新替换测试可运行 `test-update-install.ps1`，运行前关闭启动器。实际区域写入测试需显式运行 `test-region-live.ps1 -AllowSystemLocaleChanges`，仅用于维护者本机。
