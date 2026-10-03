# Snap Workspace

Snap Workspace 是一个面向 Windows 11 的工作区管理器。它保存应用、窗口身份和布局，在恢复时优先通过 Windows Shell 的原生 Snap 通道创建布局；不支持 Snap 的窗口使用一次性兼容定位，需要运行但不应出现在桌面的应用则按后台角色处理。

> 当前源码版本：**0.9.6，Route 3 工程预览版**。原生 Snap 后端依赖未经公开承诺的 Windows Shell 接口。Shell 哈希只用于诊断和兼容等级；真正的启用条件是运行类、`IsSupported`、manager 创建及 v2/v3 接口均通过运行时探测。探测失败时仍会安全拒绝原生提交，不会假装创建了 Snap Group。

安装包见 [0.9.6 Release](https://github.com/uiu8/SnapWorkspace/releases/tag/v0.9.6)。推荐下载 `self-contained` ZIP，无需另装 .NET。该版本仍为工程预览版，未代码签名；升级前请保存编辑并退出旧进程，保留现有工作区数据。

## 先了解三个角色

| 角色 | 桌面上可见 | 进入 `SnapWindows` | 恢复行为 |
|---|---:|---:|---|
| 原生吸附 | 是 | 是 | 作为完整原生布局的一部分批量提交，由 Windows Snap 管理 |
| 兼容定位 | 是 | 否 | 启动或复用窗口后，按保存区域定位一次 |
| 后台启动 | 否 | 否 | 作为恢复前置阶段启动，并在该次恢复事务内阻止可见顶层窗口 |

“兼容定位”不是“后台启动”。兼容窗口仍然显示在桌面，只是不属于原生 Snap Group；后台应用不占用任何布局槽位。

## 安装和首次使用

系统要求：

- Windows 11 x64；
- 单显示器或只使用主显示器恢复；
- 使用 `self-contained` 包时不需要另装 .NET；使用较小的 `portable` 包时需要 .NET 10 Desktop Runtime x64。

### 建议 Windows 版本

| 版本 | 正式发布时间 | 原生 v3 的理论基础 | 建议 |
|---|---|---|---|
| 22H2 | 2022-09-20 | `22621.3085`，2024-01-23 更新开始有已确认样本 | 历史下限，不建议新装 |
| 23H2 | 2023-10-31 | `22631.3085`，与 22H2 共用对应 Shell 文件 | 已有环境可测试 |
| 24H2 | 2024-10-01（普遍发布） | `26100.1` 已确认包含 v3 | 有明确接口基础 |
| 25H2 | 2025-09-30 | 与 24H2 共用核心，推定具备基础，仍需自检 | 推荐 x64 正式版并保持更新 |

这是截至 2026-10-03 的选型建议与静态接口证据，不是全部系统的运行认证。微软未公开承诺该私有接口；最终以能力检测和实际恢复结果为准。日期来源、更新 KB、样本证据及 0.9.5 / 0.9.6 的差异见 [Windows 兼容说明](docs/WINDOWS_COMPATIBILITY.md)。

### 首次使用

步骤：

1. 从 [0.9.6 Release](https://github.com/uiu8/SnapWorkspace/releases/tag/v0.9.6) 下载并解压 `SnapWorkspace-0.9.6-win-x64-self-contained.zip`，或按下文从源码构建；
2. 运行 `SnapWorkspace.exe`；
3. 查看左下角 Route 3 能力卡片；
4. 选择“新建工作区”手工编排，或选择“捕捉工作区”识别当前应用；
5. 保存后，在工作区库、通知区域菜单或命令面板中执行恢复。

本预览版尚未代码签名，Windows 可能显示未知发布者提示。应用不会注入 Explorer、修补系统文件或下载替换 DLL。

## 常用工作流

- **手工创建**：选一个受支持的 Snap Layout，把运行中或已安装应用分配给需要的槽位；未分配槽位继续显示桌面。
- **智能捕捉**：默认只列出任务栏应用，也可自由组合托盘/驻留应用和纯后台进程；逐项确认原生、兼容、后台或忽略。
- **手动点选**：智能捕捉页隐藏主窗口后，可直接点选任意顶层窗口，适合标题为空、未出现在常规清单或识别困难的程序。
- **混合恢复**：先确保后台应用无窗口，再准备可见窗口，批量提交原生 Snap，最后定位兼容窗口并处理短暂遮挡。
- **快捷使用**：命令面板可搜索全部动作和工作区；另有显示主窗口、智能捕捉、新建工作区和三个快捷工作区槽位。
- **备份迁移**：导出 `.snapworkspace` 会打包全部工作区；也可导入一个工作区 JSON。同 Id 导入不会覆盖现有数据。

## 内置原生布局

当前提供 10 个完整模型：左右二分、左/右 2:1、三列等分、两种 2/3 主窗加上下双栏、两种 1/2 单栏加上下双栏、中间主窗加两侧窄栏和四宫格。

布局允许空槽位。例如四宫格只放两个应用时，另外两个区域仍显示桌面。空槽位保留在完整原生模型中；删除区域或任意拖成非原生比例不会提交给 `SnapWindows`。

## 文档

- [文档中心](docs/README.md)：按用户、原理、开发和支持场景导航；
- [完整用户手册](docs/USER_GUIDE.md)：安装、创建、捕捉、编辑、恢复、快捷键和备份；
- [核心概念与行为边界](docs/CORE_CONCEPTS.md)：Snap Group、三种角色、空槽位与应用识别；
- [Windows 版本建议](docs/WINDOWS_COMPATIBILITY.md)：发布时间、理论接口门槛和运行时限制；
- [原生 Snap 调用原理](docs/NATIVE_SNAP_INTERNALS.md)：接口如何发现、如何调用，以及尚未解决的兼容点；
- [架构与恢复链路](docs/ARCHITECTURE.md)：模块划分、Shell 私有接口调用和事务顺序；
- [工作区数据格式](docs/WORKSPACE_FORMAT.md)：schema v5、字段、示例和迁移规则；
- [故障排查](docs/TROUBLESHOOTING.md)：原生 Snap、后台、Chrome、兼容窗口和匹配问题；
- [隐私与诊断](docs/PRIVACY_AND_DIAGNOSTICS.md)：本地日志、支持包内容和敏感选项；
- [构建与发布](docs/BUILD_AND_RELEASE.md)：开发环境、测试、打包、签名和 GitHub Actions；
- [命令行与测试命令](docs/CLI_REFERENCE.md)：能力探测、布局导出和回归测试；
- [路线图](docs/ROADMAP.md) 与 [0.9.6 发布说明](docs/RELEASE_NOTES_0.9.6.md)。

## 开发快速开始

要求 Windows 11 x64 和 .NET SDK 10.0.302（或兼容的 .NET 10 补丁版本）。

```powershell
dotnet build SnapWorkspace.Route3.slnx -c Release --nologo
dotnet run --project SnapWorkspace.App -c Release
```

运行不移动真实窗口的核心回归：

```powershell
dotnet run --project SnapWorkspace.App.SmokeTests -c Release --no-build
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- workspace-model-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- layout-recognizer-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-reuse-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- runtime-routing-test
```

真实原生布局测试需要交互式桌面和通过运行时能力探测的 Windows Shell，不能仅凭托管 CI 结果判断 Route 3 可用。

## 关键限制

- 当前只恢复主显示器，已暂缓多显示器设计；
- 当前路线三一次最多提交 4 个原生窗口，兼容和后台应用数量不限；
- 原生后端仍与私有 Windows Shell 合同耦合，跨电脑是否可用取决于运行时能力探测；未知哈希本身不再阻止运行；
- 某些应用不支持 Snap、使用启动器子进程、需要管理员权限或没有稳定窗口标题，需要兼容角色、手动点选或自定义匹配规则；
- 后台启动不是通用的“让任何程序永久无窗口运行”开关，只保证恢复事务内的无窗口状态；Chromium 浏览器使用其原生 `--no-startup-window` 协议；
- 运行中工作区状态只保存在当前进程内，完整退出 Snap Workspace 后不会猜测并重建会话所有权。

## 项目结构

| 路径 | 职责 |
|---|---|
| `SnapWorkspace.App` | .NET 10 WPF Fluent GUI、托盘、快捷键、命令面板和本地仓库 |
| `SnapWorkspace.Route3.Core` | 工作区模型、捕捉、匹配、启动编排、兼容定位与 Shell Snap 后端 |
| `SnapWorkspace.Route3.Demo` | 能力探测、诊断命令和交互式/合成回归测试 |
| `SnapWorkspace.App.SmokeTests` | 隐私、应用目录、托盘宿主和轻量 UI 回归 |
| `scripts` / `packaging` | portable、self-contained、MSIX 和发布校验 |

## 许可证

本项目采用 MIT 许可证，见 [LICENSE](LICENSE)。安全与兼容性报告方式见 [SECURITY.md](SECURITY.md)。
