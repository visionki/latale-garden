# 发布维护

Release 标题只写版本号，例如 `v1.1.3`；正文列出本次功能变化与修复。下载、运行环境和使用步骤维护在 README，不在每次 Release 重复。历史 Release 的标题和正文可直接编辑，保留标签与已有文件链接。

公开版本按正式版发布：不勾选 Pre-release，更新清单使用 `stable` 通道，将最高版本设为 Latest。历史版本保留原标签与下载文件。系统与游戏内验证范围单独记录在 README 和 VALIDATION，不用预发布标记代替兼容性说明。

## 当前支持范围

主线基于 v1.1.3 的全局临时区域切换流程，乱码修复仅支持 Windows 11 x64。进程注入实验分支已停止采用，不合入、不打包、不发布；其 Win10/Win11 游戏文字正常的短时结果不能作为持续兼容的证据。

v1.1.4 当前为未发布的回退后构建。新更新清单最低系统 Build 为 22000，避免向 Win10 推送只支持 Win11 文字兼容的新版本。保留已发布 v1.1.3 的 EXE、标签、签名更新清单和历史下载地址，不能用本地构建覆盖旧附件。

## 发布顺序

1. 同步 `Program.Version`、`AssemblyInfo.cs` 与 `app.manifest` 的版本，将新构建的 `Program.ReleaseChannel` 设为 `stable`；更新 CHANGELOG，准备 `docs/releases/vX.Y.Z.md`。
2. 构建并运行检查。最后一次构建后，使用即将上传的同一个 EXE 生成更新清单。
3. 创建对应标签与草稿 Release，标题为 `vX.Y.Z`，正文读取更新说明文件，上传 `LaTaleGarden.exe`。核对上传大小及 SHA-256，再以正式版发布（`prerelease: false`），将最新版本设为 Latest（`make_latest: "true"`）。
4. **Release 文件可以下载之后**，将签名后的清单提交到 `main` 的固定地址。可以先推发布分支和标签、上传并发布 Release，最后将该提交快进到 main。
5. 从公开地址读取清单并验证签名，下载 EXE，核对大小、SHA-256 和内嵌版本。不要重建或替换已发布版本的二进制；修复应使用新版本号。

发布前运行 `test-published-update.ps1 -Local` 检查签名后的本地清单。发布后运行 `test-published-update.ps1`，通过生产下载代码验证 GitHub 上的清单、公告和 EXE；此检查只下载、校验，不安装下载的程序。

```powershell
.\build.ps1
.\test.ps1
.\test-ui.ps1
.\test-portable.ps1
.\test-updates.ps1
# 以下检查使用本机发布密钥；先关闭真实启动器。会在测试目录打开并关闭测试启动器。
.\test-update-install.ps1

.\prepare-update.ps1 -NotesFile .\docs\releases\v1.1.3.md -Channel stable
.\sign-document.ps1 -InputFile .\updates\announcements.source.json -OutputFile .\updates\announcements.json
```

发布附件只需 EXE。GitHub 自动提供的源码归档是开发者使用的源码，不是运行包。

已发布版本转为正式版时，只修改 Release 状态与更新清单中的通道，重新签名清单；保留原 EXE、版本号和摘要。v1.1.3 按此方式转为正式版，已安装客户端的更新偏好保持原值；关闭测试版更新也能接收 `stable` 通道的新版本。

## 签名与文件格式

`updates.source.json` 与 `announcements.source.json` 是供维护者编辑、审查的明文。客户端读取的是 `updates.json`、`announcements.json`：它们包含 Base64 编码的原始 UTF-8 内容和 RSA PKCS#1 v1.5 / SHA-256 签名，先验签再解析。清单包含版本、通道、最低系统要求、说明、固定 Release 下载地址、大小与 SHA-256。

客户端内置公钥 `Assets/update-public-key.xml`。发布私钥用当前 Windows 用户的 DPAPI 保护，存放于：

```text
%LOCALAPPDATA%\LaTaleGardenPublisher\update-key.dpapi
```

私钥不属于仓库或 Release，客户端不含私钥、GitHub Token 或账户凭据。`sign-document.ps1 -InitializeKey` 只用于全新项目初始化；已有公钥或私钥时会拒绝覆盖。已有客户端固定信任这把公钥，不能随意换钥。

DPAPI 文件依赖原 Windows 用户的解密环境，复制该文件到另一台电脑不等于完成密钥迁移。迁移发布电脑前应通过受保护的方式备份、迁移签名密钥；丢失密钥时，旧客户端需要手动下载新版来建立新的信任。当前脚本使用本机签名，不依赖 GitHub Actions。

## 项目动态

编辑 `updates/announcements.source.json` 后重新签名，再把 source 和签名文件一起提交到 main。每条动态包含唯一 id、标题、正文、可选 HTTPS 链接、生效和到期时间。内容按纯文本显示，用户主动点击才打开链接。

客户端根据 id 记住“已读”，过期动态不再展示。新通知使用新 id；同一通知的小幅修订保留 id。设置中可以关闭项目动态。后续项目支持或赞助说明也使用这一入口。

自动检查与动态各自每天最多尝试一次。手动检查可立即重试；请求不包含游戏账号、电脑标识或日志。GitHub 无法访问时保留旧版和经过验证的缓存，不影响启动游戏。

## 替换与回退

下载进入当前用户的 Updates 目录，先验证签名、大小、SHA-256、程序集版本及 x64 架构。安装由临时复制的独立 EXE 执行。它检查请求进程身份、原文件摘要、区域事务锁、待恢复记录及占用进程，再等待界面退出。

旧版备份保存在 Updates/Jobs；新文件先写到目标 EXE 同一卷的临时位置，再调用 File.Replace，避免跨盘移动破坏原文件。新版在 WPF 首帧完成后，以任务随机标识、PID 和进程创建时间确认启动。45 秒内未确认或提前退出时，助手尝试关闭它并回退。

替换和回退期间持有区域事务锁。事务完成后清理目标旁边的临时文件；最近两次更新任务保留用于排查与备份，额外且超过 7 天的任务在后续启动时清理。已完成下载的缓存也保留最近两份。

启动确认只证明界面能打开，不等于所有新功能都已验证。断电、磁盘故障、杀毒软件持续锁定文件、签名密钥丢失等情况可能需要手动恢复；备份位置和错误会写入本地更新记录。网络共享和带符号链接的路径使用手动下载更新。
