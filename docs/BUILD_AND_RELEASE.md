# 构建与发布

本文说明如何从源码构建、验证并发布 Snap Workspace 0.9.6。所有命令从仓库根目录执行。

> Route 3 真实窗口测试需要交互式 Windows 桌面和通过运行时能力探测的 Shell。GitHub 托管 CI 只能覆盖不依赖真实 Snap 会话的回归，不能证明某个新 Windows build 已兼容。

## 开发环境

必需：

- Windows 11 x64；
- .NET SDK 10.0.302，或 `global.json` 允许的兼容 .NET 10 最新补丁；
- PowerShell 7 或 Windows PowerShell；
- 用于真实恢复测试的交互式桌面。

可选：

- Windows 10/11 SDK 的 `MakeAppx.exe`：构建 MSIX；
- Windows SDK 的 `SignTool.exe` 和可信 PFX：签名 MSIX；
- GitHub CLI：本地辅助发布。

确认 SDK：

```powershell
dotnet --info
dotnet --version
```

本地脚本会优先使用 `D:\software\dotnet-sdk-10.0.302\dotnet.exe`；不存在时使用当前 `PATH` 中的 `dotnet`。

## 构建和运行

```powershell
dotnet restore SnapWorkspace.Route3.slnx
dotnet build SnapWorkspace.Route3.slnx -c Release --nologo
dotnet run --project SnapWorkspace.App -c Release
```

项目：

| 项目 | 类型 | 用途 |
|---|---|---|
| `SnapWorkspace.App` | `WinExe`, WPF | 正式 GUI |
| `SnapWorkspace.Route3.Core` | 类库 | 模型、启动、捕捉和系统后端 |
| `SnapWorkspace.Route3.Demo` | 控制台/测试宿主 | 探测、诊断和回归命令 |
| `SnapWorkspace.App.SmokeTests` | 测试宿主 | 应用目录、隐私和托盘回归 |

全部项目目标为 `net10.0-windows` x64。

## 分层验证

### 每次提交必须运行

```powershell
dotnet build SnapWorkspace.Route3.slnx -c Release --nologo
dotnet run --project SnapWorkspace.App.SmokeTests -c Release --no-build
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- workspace-model-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- window-matching-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- preflight-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- layout-recognizer-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-reuse-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- runtime-routing-test
```

### 修改布局或兼容定位时

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- compatibility-placement-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- partial-layout-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- custom-layout-test
```

`partial-layout-test` 会创建临时窗口并验证四宫格只占两个 Zone；`custom-layout-test` 验证任意几何和删除 Zone 在调用 Shell 前被阻止。

### 修改后台链路时

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-window-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-descendant-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-reuse-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-executable-test "C:\path\to\app.exe"
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-aumid-test "PackageOrDesktopAumid"
```

指定 EXE/AUMID 的命令会实际启动程序，只能在可控桌面运行。

### 修改原生 Shell 后端时

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- probe
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- self-test
```

`probe` 只读；`self-test` 会依次创建、吸附并关闭临时窗口。记录测试所用 Windows build、Shell DLL 版本和 SHA-256。

### UI 与无障碍预览

发布工作流会以隐藏进程运行预览参数，输出 PNG 和无障碍报告：

```powershell
$preview = Join-Path $env:TEMP 'snapworkspace-preview.png'
$report = Join-Path $env:TEMP 'snapworkspace-accessibility.txt'
& '.\SnapWorkspace.App\bin\Release\net10.0-windows\SnapWorkspace.exe' `
  --render-preview $preview `
  --settings-preview `
  --accessibility-test-output $report
Get-Content $report
```

变更 UI 时还应人工检查浅色、深色、高对比度、键盘顺序、缩放和小窗口尺寸。

## 版本号同步

发布新版本前检查：

- `SnapWorkspace.App/SnapWorkspace.App.csproj` 的 `Version`、`FileVersion`、`AssemblyVersion`；
- `SnapWorkspace.Route3.Core/SnapWorkspace.Route3.Core.csproj` 的 `Version`；
- `scripts/Build-Release.ps1` 默认版本；
- `.github/workflows/release.yml` 手动触发默认版本；
- `docs/RELEASE_NOTES_<version>.md`；
- `README.md` 和文档中心的当前版本文字。

发布脚本通过 `-p:Version` 覆盖编译版本，但源码中的默认版本仍应同步，避免本地运行显示旧值。

## 构建发布包

```powershell
.\scripts\Build-Release.ps1 -Version 0.9.6
```

默认输出：

```text
artifacts\release-0.9.6\
├─ SnapWorkspace-0.9.6-win-x64-portable.zip
├─ SnapWorkspace-0.9.6-win-x64-self-contained.zip
├─ SnapWorkspace-0.9.6-win-x64.msix       # 安装 Windows SDK 时
└─ SHA256SUMS.txt
```

两个 ZIP 都包含程序、`LICENSE`、根 `README.md`、`CONTRIBUTING.md`、`SECURITY.md`、完整 `docs` 目录和一份位于包根目录的对应版本发布说明。这样 README 的离线文档链接在解压后仍然有效。框架依赖包要求目标机安装 .NET 10 Desktop Runtime x64；self-contained 包使用单文件、内嵌原生库并启用压缩。

脚本要求 `OutputRoot` 位于仓库内，并会删除目标发布目录后重建。不要把输出指向包含用户数据的目录。

自定义仓库内输出：

```powershell
.\scripts\Build-Release.ps1 `
  -Version 0.9.6 `
  -OutputRoot '.\artifacts\candidate-0.9.6'
```

## 构建和签名 MSIX

只要求生成 MSIX，缺少 Windows SDK 时失败：

```powershell
.\scripts\Build-Release.ps1 -Version 0.9.6 -RequireMsix
```

明确只构建两个 ZIP、不生成 MSIX：

```powershell
.\scripts\Build-Release.ps1 -Version 0.9.6 -SkipMsix
```

签名：

```powershell
.\scripts\Build-Release.ps1 `
  -Version 0.9.6 `
  -RequireMsix `
  -Publisher 'CN=Your Trusted Publisher' `
  -PfxPath 'C:\secure\SnapWorkspace.pfx' `
  -PfxPassword '<从安全秘密存储读取>'
```

要求：

- `Publisher` 必须和证书 Subject、MSIX manifest 一致；
- PFX 和密码不得提交到仓库；
- 未签名 MSIX 只能作为测试产物，不能称为可信安装程序；
- 公共发布应使用受信任证书并验证签名链。

## 校验产物

### SHA-256

```powershell
$release = '.\artifacts\release-0.9.6'
Get-Content "$release\SHA256SUMS.txt"
Get-FileHash "$release\SnapWorkspace-0.9.6-win-x64-self-contained.zip" -Algorithm SHA256
```

### 解压和启动冒烟

```powershell
$candidate = Join-Path $env:TEMP 'SnapWorkspace-0.9.6-candidate'
Expand-Archive `
  '.\artifacts\release-0.9.6\SnapWorkspace-0.9.6-win-x64-self-contained.zip' `
  -DestinationPath $candidate `
  -Force
& "$candidate\SnapWorkspace.exe"
```

确认：

- 文件版本为目标版本；
- 单实例激活有效；
- Route 3 能力卡片给出明确结果；
- 现有 `%LOCALAPPDATA%\SnapWorkspace` 数据可读取；
- 新建、捕捉、恢复、托盘、命令面板和支持包至少完成一轮；
- 彻底退出后无 Snap Workspace 进程残留。

不要让候选测试覆盖唯一用户数据。重要环境先备份 `.snapworkspace` 和本地数据目录。

## GitHub Actions

### `build.yml`

对 push 和 pull request：

- Release 构建；
- 工作区模型；
- 布局识别；
- 后台复用；
- 应用支持和托盘冒烟。

不运行真实 Route 3 窗口移动，因为托管 runner 没有适合验证的交互桌面和固定 Shell 会话。

### `release.yml`

触发方式：

- 手动 `workflow_dispatch` 输入版本；
- 推送 `v*` 标签。

流程执行构建、核心测试、无障碍预览、可选证书恢复、发布打包、artifact 上传、GitHub build provenance attestation，并在标签触发时创建 GitHub Release。

需要的可选 secrets：

- `MSIX_PFX_BASE64`；
- `MSIX_PFX_PASSWORD`；
- `MSIX_PUBLISHER`。

三个签名 secret 未配置时，发布工作流会传入 `-SkipMsix`，只上传两个 ZIP 和校验和；配置有效证书后才要求构建并签名 MSIX。工作流不会把未签名 MSIX 作为公开下载项。

## 发布前清单

- [ ] 版本号全部同步；
- [ ] 发布说明只描述已实现行为；
- [ ] Release 构建零错误，警告已审查；
- [ ] 非交互回归全部通过；
- [ ] 交互式 Snap、部分布局和混合角色工作区通过；
- [ ] 后台协议用真实目标应用验证；
- [ ] 浅色、深色、高对比度和键盘检查通过；
- [ ] 默认支持包没有路径、标题或完整定义泄漏；
- [ ] 两个 ZIP 在干净目录启动成功；
- [ ] SHA-256 与实际文件一致；
- [ ] MSIX 已可信签名，或明确标为测试产物；
- [ ] 在至少一个现有用户数据副本上验证迁移；
- [ ] 文档中心、README 和当前发布说明链接有效。

## 变更必须维护的边界

- 任意几何不能直接进入 `SnapWindows`；
- 兼容窗口可见但不属于 Snap Group；
- 后台必须在恢复事务内无可见顶层窗口；
- 未知哈希进入运行时兼容探测；运行类、manager 或必需接口失败时必须安全关闭原生路径；
- 诊断不得自动上传，新增敏感字段默认排除；
- 在多显示器设计正式恢复前，不向 schema 偷渡未验证的显示器语义。
