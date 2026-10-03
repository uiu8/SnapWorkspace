# Snap Workspace 0.9.6

0.9.6 调整了 Windows 更新后的 Route 3 兼容策略。`windowsudk.shellcommon.dll` 的精确 SHA-256 不再是软件或原生 Snap 路径的硬门槛。

> Route 3 工程预览版，未代码签名。接口基础和本机验证不等于所有 Windows 11 版本均兼容。

## 下载与升级

- `SnapWorkspace-0.9.6-win-x64-self-contained.zip`：推荐，无需另装 .NET。
- `SnapWorkspace-0.9.6-win-x64-portable.zip`：较小，需要 .NET 10 Desktop Runtime x64。
- `SHA256SUMS.txt`：用于核对两个 ZIP 的 SHA-256。
- 本次不发布未签名 MSIX。保存编辑并完全退出旧进程后再运行新版；保留 `%LOCALAPPDATA%\SnapWorkspace` 中的设置和工作区。

## 改进

- Shell 兼容状态改为三态：
  - `KnownBaseline`：哈希已知且运行时能力探测通过；
  - `RuntimeCompatible`：哈希未知，但 RuntimeClass、`IsSupported`、manager 创建和 v2/v3 接口均通过；
  - `Unavailable`：运行时能力探测失败，原生路径安全关闭。
- DLL 路径、版本与 SHA-256 继续写入本地诊断和支持包，用于定位 Windows 更新后的兼容性变化。
- GUI 能区分“已验证基线”和“运行时兼容”，避免把未知哈希误报为路线三不可用。
- `SnapWindows` 仍只接受内置原生模型，并继续执行 DWM 几何结果验证；本次变更没有放宽布局或成功判定。

## 兼容性

- 工作区格式仍为 schema v5，不需要迁移。
- 用户设置、工作区、捕捉规则和诊断保留在 `%LOCALAPPDATA%\SnapWorkspace`，升级不会覆盖。
- 0.9.5 及更早版本在 Windows 更新改变 Shell 哈希后可能直接禁用路线三；0.9.6 会改用运行时接口探测。

## 文档补充

- [Windows 版本建议](WINDOWS_COMPATIBILITY.md)：版本发布时间、最早确认的 v3 样本和理论兼容边界。
- [原生 Snap 调用原理](NATIVE_SNAP_INTERNALS.md)：发现接口、运行时调用与剩余兼容风险，附脱敏的 DLL / PDB 证据索引。
- 两个 ZIP 均附带完整中文文档和本版本说明。

## 安全边界

该接口仍是 Windows 私有 Shell 合同。未知哈希不代表自动兼容：只有完整运行时探测通过才允许提交，任一步失败都会返回不可用并保留诊断信息。
