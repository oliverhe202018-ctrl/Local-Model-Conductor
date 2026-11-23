# Phase 4B.5 — Initial Audit & Pipeline Hardening Plan

**Document ID:** `PHASE4B5-AUDIT-20261123-01`
**Execution Mode:** `EXECUTE_ISOLATED_SOURCE_REPAIR`
**Working Directory:** `D:\project\本地模型计算\phase4b_5_worktree`
**Branch:** `phase4b-5-publication-hardening`
**Base Commit:** `17c09a2e0d6c464ac601c7d72326fcd6607af960` (verified against remote `main`)

---

## 1. 现有实现与架构基线 (Existing Implementation Baseline)

1. **构建入口**：
   - `LocalModelConductor.sln` 涵盖全量应用及子系统：
     - `src/LocalLlmConsole.Core`: 核心基础设施与模型抽象。
     - `src/LocalLlmConsole.App`: WPF 桌面管理主体。
     - `src/LocalLlmConsole.ControlCli`: Native AOT 命令行管理工具 `llwmctl`。
     - `src/Guard/*`: Guard 安全控制面（`Contracts`, `Service`, `Cli`, `Tests`）。
     - `tests/*`: 自动化测试套件（`LocalLlmConsole.Tests`, `LocalLlmConsole.UiTests`, `LocalLlmConsole.FakeRuntime`）。
2. **已有成熟发布与测试组件**：
   - `scripts/check-code-shape.ps1`: 架构行数和内聚度静态门禁。
   - `scripts/test-release-gate.ps1`: 综合 Release 门禁（编译、单元测试、覆盖率、代码格式、文档链接）。
   - `scripts/publish-app.ps1`: Self-Contained 单文件与 Native AOT 发布脚本。
   - `installer/LocalModelConductor.nsi`: 基于 NSIS 的 Windows 独立安装程序脚本（Per-User、多语言、保留数据卸载）。
   - `install.ps1`: PowerShell 一键安装 Bootstrapper（支持 GitHub Releases 查询、SHA-256 校验、本地 Manifest 及 DryRun）。
   - `.github/workflows/build.yml` & `.github/workflows/release.yml`: GitHub Actions 工作流。

---

## 2. 已确认问题 (Confirmed Issues)

| 编号 | 模块 | 现象与根因 | 影响 |
|---|---|---|---|
| **ISSUE-1** | CI 门禁 (`build.yml`) | `build.yml` 中设置了 `continue-on-error: true`，且仅执行 `LocalLlmConsole.Tests`，遗漏了 `UiTests` 与 `Guard.Tests`。测试失败无法阻断 CI。 | CI 无法有效拦截回归。 |
| **ISSUE-2** | 架构门禁 (`check-code-shape.ps1`) | 1. `src/Guard/LocalLLMManager.Guard.Service/Control/GuardControlCoordinator.cs` 达 746 行（非空白上限 500 行）。<br>2. `src/Guard/LocalLLMManager.Guard.Tests/Phase4AGuardAcceptanceTests.cs` 因置于 `src/` 下被当成生产代码扫描，且行数 932 行（上限 500 行；即便识别为测试代码亦超 800 行上限）。 | `ci.yml` 与 `test-release-gate.ps1` 静态门禁直接失败阻断。 |
| **ISSUE-3** | NSIS 安装器路径 (`LocalModelConductor.nsi`) | 1. `SOURCE_DIR` 硬编码指向开发机历史目录 `..\..\phase4b_3_release\dist\...`。<br>2. `MUI_ICON` 指向 `..\..\phase4b_windows_manager_fork\repo\...`。<br>3. `OUTPUT_DIR` 与工作流期望的 `dist/release` 不匹配。 | GitHub Actions runner 或干净机器上 makensis 找不到文件，打包必崩。 |
| **ISSUE-4** | NSIS 依赖文件完整性 | NSIS 脚本声明复制 `.sha256` 伴随哈希、`web.config`、`licenses/*.*`、`docs/CONTROL_API.md`、`sbom.spdx.json` 等，但 `release.yml` 未在 makensis 前统一准备这些文件。 | 打包过程报文件缺失异常。 |
| **ISSUE-5** | Release Manifest 兼容性 | `release.yml` 生成的 `release-manifest.json` 使用 `artifacts` 数组，而 `install.ps1` 本地 JSON Manifest 解析代码试图读取顶层的 `$manifest.installerAsset` 与 `$manifest.sha256`，结构不兼容。 | 一键安装脚本在读取 Release Manifest 时无法提取正确的安装包文件名与哈希。 |
| **ISSUE-6** | 安装脚本空版本容错 | 在当前 GitHub 尚未发布首个 Release 的情况下，`install.ps1` 请求 GitHub API 得到空数组，错误提示不够友好，未明确提示用户当前处于公开预发布阶段。 | 用户直接运行一键安装体验不良。 |
| **ISSUE-7** | README 安装说明脱节 | README 引导用户直接运行一键安装或前往 Releases 下载，但尚未声明首发正式包处于 Pre-release 阶段，导致用户误认为安装包现已可用。 | 用户预期与实际状态不符。 |

---

## 3. 潜在问题与风险 (Potential Risks)

1. **NuGet Lock File 冲突**：修改项目或依赖可能导致 `--locked-mode` 下 restore 失败。必须遵守锁定策略，绝不随意升级无关依赖。
2. **测试项目路径与引用**：将 `LocalLLMManager.Guard.Tests` 规范化迁移至 `tests/` 目录时，需确保项目引用（`.csproj`）、`.sln` 以及测试运行脚本中的相对路径同步对齐。
3. **PDB 泄露与发布干净度**：Release 单文件发布必须确保排除调试 PDB，保持干净紧凑。

---

## 4. 准备复用的成熟机制与修复策略 (Reuse & Repair Strategy)

1. **测试门禁强化**：
   - 在 `build.yml` 中移除 `continue-on-error: true`。
   - 纳入全部适用测试项目：`LocalLlmConsole.Tests`, `LocalLlmConsole.UiTests`, `LocalLLMManager.Guard.Tests`。
   - 保留 TRX 测试报告输出，并使用 `if: always()` 确保即使测试失败也上传报告供排查。
2. **代码行数门禁修复**：
   - 采用 C# 推荐的 `partial class` 架构将 `GuardControlCoordinator.cs` 拆分为核心逻辑与生命周期/切换逻辑。
   - 将 `LocalLLMManager.Guard.Tests` 移入 `tests/` 目录，并在逻辑上拆分 `Phase4AGuardAcceptanceTests.cs` 为两个专注测试类，使两类均低于 800 行硬上限。
3. **构建输出与 NSIS 路径统一**：
   - 建立标准构建输出约定：
     `dist/LlamaCppWindowsManager-win-x64/` (发布二进制与支撑文件)
     `dist/release/` (安装器、便携包、校验和、清单)
   - 在 `LocalModelConductor.nsi` 中定义标准默认路径，并支持通过 `/DSOURCE_DIR=...` 与 `/DOUTPUT_DIR=...` 显式覆盖。
   - `release.yml` 引入打包前置准备步骤（复制 docs、licenses、生成伴随 sha256 等）与必需文件校验门禁。
4. **Manifest 与 install.ps1 接口对齐**：
   - 统一 Manifest 格式，`install.ps1` 同时支持通过 `artifacts` 数组匹配对应资产的 `sha256`。
   - 增强一键安装脚本的空 Release 状态提示。
5. **文档状态纠偏**：
   - 更新 `README.md` 与 `README.zh-CN.md`，增加 `Available after the first official release` 说明。
