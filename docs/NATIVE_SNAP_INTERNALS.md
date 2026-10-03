# 原生 Snap 接口如何发现、如何调用

本文说明接口发现依据与软件运行时调用方式；不提供对私有 Windows ABI 的跨版本保证。版本建议见 [Windows 兼容说明](WINDOWS_COMPATIBILITY.md)，完整恢复事务见 [架构说明](ARCHITECTURE.md)。

**核心分工：Snap Workspace 保存工作区、启动或匹配应用、计算布局；Windows Shell 执行原生吸附。**兼容定位是独立路径，不把普通窗口定位冒充成 Snap Group。

## 如何发现接口

1. **读取系统元数据。**研究环境中的 `windowsudk.winmd` 提供运行类、接口 GUID、方法及参数信息，可定位 `WindowsUdk.UI.Shell.SnapLayoutManager`。元数据中的合同版本不是 Windows OS build，不能直接当成最低系统版本。
2. **分析实际实现。**检查 `windowsudk.shellcommon.dll` 的 PE 身份、导入和导出；使用文件的 RSDS 标识取得微软服务器上的匹配 PDB，再结合二进制分析核对接口集合、方法参数及虚函数表位置。`SnapWindows` 不是可直接按同名 DLL 导出取得的普通函数。
3. **运行时确认。**通过工厂和 `QueryInterface` 查询接口是否存在；再用受控测试窗口验证提交和结果。接口存在性、窗口几何和 Snap Group 交互需要分层验证。
4. **跨版本取样。**本次历史核查读取了 11 份 x64 DLL、其中 7 份匹配 PDB，发现 `.3007` 尚无 v3，而 `.3085` 已有实际 `SnapWindows` 实现。详见 [证据记录](evidence/snap-interface-history-2026-10-03.json)。

公开 PDB 是调试符号，不等于微软发布了组件完整源代码或承诺第三方可长期依赖该接口。研究过程也不需要修补系统 DLL 或将历史 DLL 加载进当前系统。

## 运行时调用链

1. 启动或复用目标应用，找到对应 HWND；后台角色不进入原生窗口集合。
2. 根据完整原生模型、主显示器工作区域和 DPI 上下文，准备窗口标识数组与目标矩形数组。
3. 初始化适当的 WinRT / 线程上下文，调用 `RoGetActivationFactory`，取得 `WindowsUdk.UI.Shell.SnapLayoutManager` 的静态接口。
4. 能力探测调用 `IsSupported`；通过 `ISnapLayoutManagerStatics2::CreateForWorkAreaRect` 创建对应工作区域的管理器。
5. 使用 `QueryInterface` 查询 `ISnapLayoutManager3`，取得 v3 对象指针。
6. 通过该对象的虚函数表调用 `SnapWindows`，提交两组等长数组。
7. 检查 HRESULT；调用链等待验证时机后读取窗口实际边界并核对位置。失败会报告，不对原生窗口偷偷追加兼容定位。

### 关键标识与调用形式

| 项目 | 值或含义 |
|---|---|
| Runtime class | `WindowsUdk.UI.Shell.SnapLayoutManager` |
| v3 IID | `2de3f20f-38ae-50d4-b853-b440353091dc` |
| v3 方法 | `SnapWindows` |
| 虚函数表槽位 | 6，从 0 开始；此前是 IUnknown / IInspectable 的基础方法 |
| 输入 | 窗口数量、WindowId 数组、矩形数量、Rect 数组 |
| 返回 | HRESULT；失败时保留具体阶段和错误值 |

当前 C# 实现通过 `Marshal.GetDelegateForFunctionPointer` 调用该 ABI，并在调用期间固定两组托管数组，结束后释放固定句柄与 COM 对象。它取得系统返回的接口对象，**不把 DLL 内某个函数的绝对地址或 RVA 写死**；但已知 IID、槽位和参数布局仍属于私有 ABI 假设，不会因此自动获得永久兼容保证。

## 为什么不是打开 Snap 菜单再移动窗口

当前原生路径直接调用 Shell 的批量吸附方法，不需要模拟用户点击最大化按钮或 Win+Z 菜单。目标矩形只是给 Shell 的布局请求，并不意味着任意矩形都能成为原生布局。

- 原生集合只接受当前验证过的完整模型；模型允许空槽位，只有已分配窗口进入提交。
- 兼容集合通过独立的窗口定位后端处理，窗口可以可见，但不据此宣称属于原生 Snap Group。
- 后台集合不占原生槽位，启动与可见性管理由恢复事务处理。

系统是否接受窗口，还受窗口自身大小约束、权限、创建时序等因素影响。不能把 HRESULT 成功或某次位置正确写成“所有 Snap Group 语义已验证”。

## 当前仍需完善的兼容点

当前 `CurrentBuildHwndWindowIdProvider` 检查 HWND 有效后，仍将其位值转换为 `ulong` 作为 WindowId。这是已有环境验证过的实现，不是跨系统的通用证明。

后续适配应评估 `windows.ui.interop.h` 中的 [GetWindowIdFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.interop/nf-windows-ui-interop-getwindowidfromwindow)，并核对返回的 `Windows.UI.WindowId` 与私有 ABI 一致；不要混用其他命名空间中的同名类型。本文没有把该改进描述为已经落地。

## 证据、结论与调用路径

本节是已有研究的脱敏索引，不是在所有历史系统上重新进行动态实验。范围仅限工作区布局互操作；没有系统修改、进程注入或安全绕过测试。

| 证据 | 观察 | 支持的结论 | 对应调用位置 |
|---|---|---|---|
| E-01：22621.3007 DLL / PDB | IID 与实现接口集合中均未发现 v3 | F-01：不是所有 Windows 11 初始版本都具备 v3 | 管理器接口查询阶段 |
| E-02：22621.3085 DLL / PDB | v3 IID、`SnapWindows`、v1/v2/v3 接口集合同时存在 | F-02：该公开维护分支样本已有批量提交接口基础 | `QueryInterface` → `SnapWindows` |
| E-03：22621.3155、26100.1 DLL / PDB | 存在同形 v3 方法参数布局 | F-03：接口基础跨越多个已取样版本，不局限于 296xx | WindowId / Rect 数组提交 |
| E-04：当前源码 | 工厂创建、接口查询、vtable 调用、结果验证均可定位 | F-04：产品实际走 Shell 接口，兼容定位是独立路径 | 下列源码入口 |

E-01 至 E-03 的观察日期为 2026-10-03；样本哈希、PDB 哈希、Microsoft 下载 URL、符号原文均保存在 [JSON](evidence/snap-interface-history-2026-10-03.json)。关键 `.3007` → `.3085` 边界样本哈希完全匹配索引。部分辅助样本有符号服务器键碰撞，JSON 同时记录请求版本和实际版本，不拿这些样本冒充精确请求版本。

复核时从 JSON 的 `Source` 下载指定 DLL，检查 `Sha256`；从 `Pdb.Url` 下载匹配 PDB，检查已记录的 `PdbSha256`，再用 `rabin2 -P` 核对 `PdbExcerpts`。仅下载和静态读取，不执行这些 DLL。原始二进制及完整 PDB 不随本仓库发布。静态证据置信度高，旧系统上的运行时兼容仍待行为验证。

源码入口：

- [ShellSnapBackend.cs](../SnapWorkspace.Route3.Core/ShellSnapBackend.cs)：`Probe`、`CreateManager`、`Snap`、`Verify`。
- [WindowIdProviders.cs](../SnapWorkspace.Route3.Core/WindowIdProviders.cs)：当前 HWND / WindowId 转换。
- [NativeInterop.cs](../SnapWorkspace.Route3.Core/NativeInterop.cs)：WinRT、COM 和窗口互操作定义。
- [NativeSnapLayoutCatalog.cs](../SnapWorkspace.Route3.Core/NativeSnapLayoutCatalog.cs)：原生模型约束。
- [CompatibilityWindowBackend.cs](../SnapWorkspace.Route3.Core/CompatibilityWindowBackend.cs)：独立的兼容定位路径。

该调用路径的残余风险是私有 ABI 变化、上下文差异和目标窗口不兼容；应通过分阶段诊断与实际行为测试管理，而不是宣称接口一旦存在就永久可用。
