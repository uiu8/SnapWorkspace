# 为 Snap Workspace 贡献代码

Snap Workspace 依赖私有 Windows Shell ABI。所有变更必须保持安全关闭能力门，并严格区分原生 Snap、可见兼容定位和后台启动。

## 准备环境

要求 Windows 11 x64、.NET SDK 10.0.302 或兼容的 .NET 10 补丁版本。真实 Snap 测试还需要交互式桌面。

```powershell
dotnet build SnapWorkspace.Route3.slnx -c Release --nologo
dotnet run --project SnapWorkspace.App.SmokeTests -c Release --no-build
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- workspace-model-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- layout-recognizer-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- background-reuse-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- runtime-routing-test
```

只在可控交互桌面运行 `self-test`、`partial-layout-test` 和真实后台应用命令。

## 必须维持的产品边界

- 任意几何不能提交给 `SnapWindows`，原生布局必须通过 `NativeSnapLayoutCatalog`；
- Route 2/兼容定位与 Route 3 分开。兼容窗口可见，但不是 Snap Group 成员；
- 后台表示恢复事务内没有可见顶层窗口，必要时使用应用专用协议；
- 不对原生窗口做第二次位置校正；
- Shell 哈希只用于诊断和兼容等级；运行类、`IsSupported`、manager 或必需接口探测失败时必须安全关闭 Route 3；
- 诊断不能自动上传，新增敏感字段默认关闭并添加隐私回归；
- 多显示器设计恢复前，不向 schema 添加未验证字段。

## 提交变更

PR 应说明：

- 用户可见行为和为什么需要；
- 受影响角色与恢复阶段；
- 运行过的命令和结果；
- Route 3 真实测试所用 Windows build 和 Shell 哈希；
- UI 变更的浅色/深色截图、键盘和高对比度影响；
- 数据格式变更的迁移与回退方法；
- 新诊断字段的隐私分类。

不要提交 PFX、密码、真实用户工作区、窗口标题或私有路径。

## 打包候选版

```powershell
.\scripts\Build-Release.ps1 -Version 0.9.6
```

portable 包只需要 .NET SDK；MSIX 还需要 Windows SDK。公开 MSIX 必须使用 Subject 与 manifest publisher 一致的可信证书。完整流程见 [构建与发布](docs/BUILD_AND_RELEASE.md)。
