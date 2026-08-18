# Snap Workspace 文档中心

本页是 0.9.5 中文文档的统一入口。先按你的目标选择文档；历史发布说明保留原语言，用于追踪行为演进。

## 我想使用软件

1. 从 [完整用户手册](USER_GUIDE.md) 开始，完成安装、创建或捕捉第一个工作区；
2. 对“原生吸附、兼容定位、后台启动”有疑问时阅读 [核心概念与行为边界](CORE_CONCEPTS.md)；
3. 恢复结果不正确时进入 [故障排查](TROUBLESHOOTING.md)；
4. 需要迁移、备份或手工检查 JSON 时阅读 [工作区数据格式](WORKSPACE_FORMAT.md)；
5. 需要提交问题时先阅读 [隐私与诊断](PRIVACY_AND_DIAGNOSTICS.md)。

## 我想理解实现

- [架构与恢复链路](ARCHITECTURE.md)：组件边界、原生 Shell 调用、捕捉和恢复事务；
- [核心概念与行为边界](CORE_CONCEPTS.md)：为什么不能把兼容定位伪装成 Snap Group；
- [工作区数据格式](WORKSPACE_FORMAT.md)：schema v5 实体、约束和迁移；
- [命令行与测试命令](CLI_REFERENCE.md)：探测、只读检查、示例和测试矩阵。

## 我想参与开发或发布

- [构建与发布](BUILD_AND_RELEASE.md)：环境、构建、验证、压缩包、MSIX、签名和 GitHub Actions；
- [贡献指南](../CONTRIBUTING.md)：变更必须维持的产品边界；
- [安全与兼容性](../SECURITY.md)：Shell 私有 ABI 的安全门；
- [路线图](ROADMAP.md)：已完成阶段和后续方向。

## 版本说明

当前版本：[0.9.5](RELEASE_NOTES_0.9.5.md)。

历史版本：

- [0.9.4](RELEASE_NOTES_0.9.4.md)、[0.9.3](RELEASE_NOTES_0.9.3.md)、[0.9.2](RELEASE_NOTES_0.9.2.md)、[0.9.1](RELEASE_NOTES_0.9.1.md)、[0.9.0](RELEASE_NOTES_0.9.0.md)；
- [0.8.0](RELEASE_NOTES_0.8.0.md)、[0.7.0](RELEASE_NOTES_0.7.0.md)、[0.6.1](RELEASE_NOTES_0.6.1.md)、[0.6.0](RELEASE_NOTES_0.6.0.md)；
- [0.5.2](RELEASE_NOTES_0.5.2.md)、[0.5.1](RELEASE_NOTES_0.5.1.md)、[0.5.0](RELEASE_NOTES_0.5.0.md)；
- [0.4.1](RELEASE_NOTES_0.4.1.md)、[0.4.0](RELEASE_NOTES_0.4.0.md)、[0.3.3](RELEASE_NOTES_0.3.3.md)、[0.3.2](RELEASE_NOTES_0.3.2.md)、[0.3.1](RELEASE_NOTES_0.3.1.md)、[0.3.0](RELEASE_NOTES_0.3.0.md)。

## 文档适用范围

- 软件版本：0.9.5；
- 工作区格式：schema v5；
- 平台：Windows 11 x64；
- 当前原生恢复范围：主显示器、每次最多 4 个原生窗口；
- 文档描述的是已实现行为。路线图中的功能不视为当前能力。
