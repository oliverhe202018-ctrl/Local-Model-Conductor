# Contributing to Local Model Conductor

欢迎参与 Local Model Conductor 项目的建设与开发！

## 开发准则
1. 所有代码修改必须保持与 .NET 10 及 Windows 平台的兼容性。
2. 保持控制面与数据面解耦，禁止在 UI 或 CLI 中直接存储数据面管理密码。
3. 新增功能需提供相应的单元测试或集成测试，全量测试套件需保证通过。
4. 提交 PR 前请在本地运行 `dotnet test`，确保无新增回归错误。
