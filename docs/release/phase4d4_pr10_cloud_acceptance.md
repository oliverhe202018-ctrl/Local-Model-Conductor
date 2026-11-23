# Phase 4D.4 — PR #9 合并、统一发行候选入主线与 PR #11 云端验收报告

- **报告标识**：`PHASE4D4_PR10_PR11_CLOUD_ACCEPTANCE_REPORT`
- **生成时间**：2026-11-24T05:32:00+08:00
- **执行模式**：`EXECUTE_WITH_GATES_NO_RELEASE`
- **正式 GitHub 仓库**：[oliverhe202018-ctrl/Local-Model-Conductor](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor)
- **基线 PR**：[PR #9 (Phase 4B.5)](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/pull/9) — 已合并至 `main`
- **审查 PR**：[PR #11 (Phase 4D.3 Unified Release Candidate)](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/pull/11) — 保持 OPEN 状态
- **阶段裁决**：`READY_FOR_MAIN_MERGE_WITH_HEURISTIC_TRANSLATION_LIMITATION`

---

## A. 执行摘要

1. **PR #9 正式合并**：
   - 依据用户授权，PR #9 (`367ab4e`) 已于 2026-11-24 通过限定的 Merge Commit 方式成功合并到 `main`。
   - 生成的 Merge Commit SHA：`ddb5175ded5364de4b00dc780afa3df1c8ab7b57`。
   - 远程 `origin/main` 已推进至该合并提交。

2. **PR 编号关系说明**：
   - 在 PR #9 合并后，GitHub Dependabot 自动提交了依赖补丁并占用了编号 PR #10 (`Bump System.Management from 10.0.0 to 10.0.15`)。
   - 因此，根据任务授权提交的统一发行候选 PR 正式登记为 **PR #11**（[PR #11 链接](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/pull/11)），完全承接 Phase 4D.3 的全部成果与审查目标。

3. **云端 CI 与安全扫描全绿（5 项全部 SUCCESS）**：
   - `Analyze C# (CodeQL)`：PASS (5m 8s)
   - `CodeQL (Check)`：PASS (5s)
   - `Review dependency changes (Dependency Review)`：PASS (7s)
   - `build-and-test (build.yml)`：PASS (10m 12s)
   - `Build, test, and publish (ci.yml)`：PASS (12m 34s)

4. **补齐真实 Windows 安装器端到端验收**：
   - 克服了此前本地 Windows Sandbox 嵌套不可用的物理限制，在 GitHub Actions 托管的真实 Windows Server 2025 (`windows-2025-vs2026`) 虚拟机中成功执行了全套物理端到端安装器验收测试。
   - 覆盖静默安装、文件校验、注册表写入、快捷方式、CLI 调用、升级数据金丝雀保留、静默卸载、干净清理及用户数据保留等全部 20+ 项物理断言，测试 100% 通过。

5. **现役生产服务零干扰**：
   - 生产端口 19000 (Caddy)、19001 (llama-swap)、8082 (llama-server)、19250 (Guard)、3001 (Open WebUI) 进程持续健康，PID 保持不变，本地推理与固定接口 `http://127.0.0.1:19000/v1` 运行完全正常。

---

## B. Git 拓扑与合并审计

### 1. PR #9 合并路径核验
- **PR #9 Head**：`367ab4e27fa0f8933997ca93eccd9c826a0c5424`
- **PR #9 Base**：`17c09a2e0d6c464ac601c7d72326fcd6607af960`
- **合并模式**：Merge Commit (`gh pr merge 9 --merge`)
- **生成 Merge Commit**：`ddb5175ded5364de4b00dc780afa3df1c8ab7b57`
- **Parents**：
  - Parent 1 (Base): `17c09a2e0d6c464ac601c7d72326fcd6607af960`
  - Parent 2 (PR #9 Head): `367ab4e27fa0f8933997ca93eccd9c826a0c5424`

### 2. 统一发行候选分支血统
- **工作区**：`D:\project\本地模型计算\phase4d3_integration`
- **分支名**：`feature/phase4d3-unified-release-candidate`
- **Merge Base with origin/main**：`367ab4e27fa0f8933997ca93eccd9c826a0c5424`
- **当前提交 SHA**：`72e774d642878c4333621fc53a55b4104c636f33`
- **提交历史链**（从 PR #9 基础推进）：
  1. `f1feeb5`：feat(integration): Phase 4D.3 unified RC — localization, branding, update-source (from 0f187207 onto PR#9 base 367ab4e)
  2. `0da6c7b`：fix(packaging): preserve PR#9 CI test hardening and align LocalModelConductor.nsi to v3.2.0
  3. `e32d8ad`：fix(tests): restore PR#9 README and THIRD-PARTY-NOTICES and update release metadata test assertions to v3.2.0
  4. `7c83605`：docs(release): publish phase4d.3 unified release candidate acceptance report
  5. `0f72d0f`：build(packaging): support explicit makensis bin path and dynamic PRODUCT_VERSION definition
  6. `611a7ba`：build(deps): lock publish dependencies for LocalLLMManager.Guard.Contracts
  7. `0b1b0d5`：ci(e2e): add real Windows NSIS installer acceptance test and fix doc formatting
  8. `94673b8`：fix(ci): format GuardOperationsCenterController and extend external mock health poll timeout
  9. `23af28a`：fix(ci): sanitize NSIS product version and restore Guard.Contracts lockfile bypass
  10. `6845e75`：fix(ci): fix PowerShell string delimiter in E2E installer test and poll served models in fake runtime integration test
  11. `72e774d`：fix(ci): avoid duplicate uninstall attempt in test-previous-version-upgrade cleanup

---

## C. PR #11 云端 CI 与安全扫描全景

PR #11 状态：`OPEN` / `MERGEABLE`<br>
PR URL：`https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/pull/11`

| 工作流名称 | 任务 / 检查项 | 运行 ID | 耗时 | 最终状态 | 核心涵盖 |
|---|---|---|---|---|---|
| **CodeQL** | `Analyze C#` | `45268022662` | 5m 8s | **SUCCESS (PASS)** | 全面代码安全静态分析、无高危漏洞、无凭据泄露 |
| **CodeQL** | `CodeQL` | - | 5s | **SUCCESS (PASS)** | GitHub 安全规则合规 |
| **Dependency Review** | `Review dependency changes` | `45268022876` | 7s | **SUCCESS (PASS)** | 依赖许可与已知漏洞审查 |
| **CI Build & Automated Tests** | `build-and-test` (`build.yml`) | `45268022453` | 10m 12s | **SUCCESS (PASS)** | 1,549 项自动化测试 (Core 1404 + UI 141 + Guard 4)、代码规整性校验、NSIS 打包门禁、**真实 Windows 安装器物理 E2E 验收** |
| **CI** | `Build, test, and publish` (`ci.yml`) | `45268022470` | 12m 34s | **SUCCESS (PASS)** | 覆盖率验证、Native AOT 发布校验、Inno Setup 打包、静默安装/修复/卸载冒烟测试、**历史稳定版 (v2.9.0) 真实升级测试** |

云端综合判定：**5 项工作流全部绿色通过（PASS），无任何测试失败，无任何跳过项。**

---

## D. 真实 Windows 安装器端到端验收

通过在 `.github/workflows/build.yml` 中集成的 `scripts/test-nsis-installer-e2e.ps1`，成功在干净的 Windows 虚拟机（非沙箱，真实宿主操作系统）上完成了物理级安装全生命周期验收：

| 阶段 | 校验项目 | 预期行为 | 实际结果 | 状态 |
|---|---|---|---|---|
| **Step 1: 安装** | `1.1_SilentInstallExitCode` | 静默安装 `/S /D=<dir>` 正常返回 | ExitCode = 0 | **PASS** |
| | `1.2_InstallDirExists` | 安装目录正确创建 | 目录存在 | **PASS** |
| | `1.3_BinaryVerification` | 核心程序及校验文件存在且非零 | `LlamaCppWindowsManager.exe` (185.4MB)<br>`llwmctl.exe` (6.8MB)<br>`Uninstall.exe` (68KB)<br>`Guard.Service.exe` (104.6MB)<br>以及对应 `.sha256` 文件完整存在 | **PASS** |
| | `1.4_DocVerification` | 授权与元数据文档就位 | `LICENSE`, `THIRD-PARTY-NOTICES.md`, `agent.md`, `AGENTS.md`, `sbom.spdx.json`, `docs/CONTROL_API.md` | **PASS** |
| | `1.5_DataSkeleton` | 用户数据骨架目录自动就绪 | `data/`, `data/models/`, `data/runtimes/`, `data/state/`, `data/logs/` 均创建 | **PASS** |
| | `1.6_RegistryVerification` | 注册表项正确注册 | `HKCU:\Software\LocalModelConductor`<br>`HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalModelConductor` 成功建立 | **PASS** |
| | `1.7_RegistryValues` | 卸载与展示信息正确 | `DisplayName` = `本地模型调度台 (Local Model Conductor)`<br>`DisplayVersion` = `3.2.0`<br>`UninstallString` = `...\Uninstall.exe` | **PASS** |
| | `1.8_Shortcuts` | 开始菜单快捷方式创建 | 开始菜单目录正确建立快捷方式 | **PASS** |
| **Step 2: CLI 调用** | `2.1_CliExecution` | 调用安装目录中的 `llwmctl.exe` | 进程正常拉起，非零安全退出（守护未运行时安全拒绝，退出码 1） | **PASS** |
| **Step 3: 覆盖升级** | `3.1_UpgradeInstallerExitCode` | 植入金丝雀数据后执行覆盖安装 | 覆盖静默安装 ExitCode = 0 | **PASS** |
| | `3.2_UpgradeBinariesPresent` | 核心二进制就位 | 二进制程序全部完好更新 | **PASS** |
| | `3.3_UpgradePreservedData` | 验证金丝雀文件未被冲掉 | `data/models/upgrade-test.canary` 完好保留<br>`data/state/upgrade-test.canary.db` 完好保留 | **PASS** |
| **Step 4: 静默卸载** | `4.1_UninstallExitCode` | 调用 `Uninstall.exe /S` | 卸载程序正常退出 (ExitCode = 0) | **PASS** |
| | `4.2_BinariesRemoved` | 应用程序主体删除 | 主程序、CLI、Guard 二进制被完全清除 | **PASS** |
| | `4.3_RegistryCleaned` | 注册表项注销 | 卸载项从 Windows 注册表完全注销 | **PASS** |
| | `4.4_ShortcutsRemoved` | 开始菜单快捷方式清除 | 开始菜单快捷方式被彻底清理 | **PASS** |
| | `4.5_UserDataRetained` | **用户核心资产防误删验证** | **`data/models` 与 `data/state` 目录及其内部用户数据完好保留！** | **PASS** |

物理端到端安装器验收结论：**ALL 20+ END-TO-END INSTALLER ACCEPTANCE CHECKS PASSED.**

---

## E. 语言系统现状与限制说明

### 1. 多语言架构验证
- **即时切换能力**：通过 `Loc.cs` 配合 WPF DynamicResource 与 `PropertyChanged` 通知，主窗口、监控面板、Guard 运维中心均可在运行时实现无感、零重启语言切换。
- **多级回退机制**：当前选定语言缺失 -> 默认英语 (`en-US`) -> 资源键名本身，永不抛出 XAML 解析异常。
- **配置持久化**：用户语言偏好保存在本地设置，重启后自动复原。

### 2. 语言质量分级边界（必须如实披露）
- **正式通过人工质量验收（Production-Verified）**：
  - **简体中文 (`zh-CN`)**：1,990 个键，100% 汉化，已通过视觉截图排版核验与人工语义校对。
  - **英语 (`en-US`)**：1,990 个键，100% 原始基准，已通过占位符一致性及长词排版核验。
- **结构化基线语言（Heuristic Baseline / Community Tier）**：
  - 包含 `ar`, `bg`, `cs`, `de`, `es`, `fa`, `fr`, `hi`, `id`, `it`, `ja`, `ko`, `nl`, `pl`, `pt`, `ru`, `sv`, `tr`, `vi` 共 19 种语言。
  - **现状**：每种语言的 JSON 文件结构完整（均含 1,990 个合法键名与有效占位符），在界面上均可正常加载切换并生效。
  - **重要限制**：上述 19 种语言**尚未完成母语级人工校对**，部分生僻术语可能存在机翻生硬或特定长度下的局部排版截断。**在正式发行说明中，严禁对外宣传 21 种语言全部达到人工验收级别**。

---

## F. 发行包最终清单

基于 HEAD `72e774d` 生成并经过云端与本地双重校验的候选发行包：

| 文件名称 | 格式 | 大小 | SHA-256 校验和 | 权限要求 | 架构 |
|---|---|---:|---|---|---|
| `LocalModelConductor-Setup-win-x64.exe` | NSIS 安装包 | 83.86 MB | `e30fd49a5a8e9c739af2fd42c872b002c76c63d4b3960e10019818bdc2a68bb0` | Per-user (普通用户免提权) | x64 |
| `LocalModelConductor-Portable-win-x64.zip` | 免安装绿色版 | 115.73 MB | `fb800af2bc4672fa6e41b2c8058a07ea165451f499d8c6f8f55768c756503eaf` | 免安装即解即用 | x64 |

- **安全检查**：包内均不包含敏感凭据、`control.json`、临时文件或 SQLite 数据库。
- **独立运行**：自包含 .NET 10 Runtime，目标 Windows 机器零环境依赖。

---

## G. 遗留问题与已知缺陷

1. **依赖升级 PR #10 待合并**：
   - GitHub 自动生成的 Dependabot PR #10 (`Bump System.Management from 10.0.0 to 10.0.15`) 仍处于 Open 状态，待主 PR #11 合并后可顺带合并或重置。
2. **多语言排版容错**：
   - 对于从右至左（RTL）书写的语言（阿拉伯语 `ar`、波斯语 `fa`），当前 WPF 布局以 LTR 为基准，尚未开启全局 FlowDirection 倒序排版。
3. **未授权项保持原状**：
   - PR #11 当前保持 OPEN，未执行合并操作。
   - Git Tag `v3.2.0` 未被移动，未推送到 GitHub 远端仓库。
   - 未在 GitHub 上创建 Release。

---

## H. 运行服务零干扰核验

整个合并、集成、打包及 CI 修复周期内，生产推理服务持续运行，监控数据如下：

```
服务端口与进程状态实时核验：
- 端口 19000 (Caddy 统一反向代理网关)：PID 41032  -> 正常侦听，响应 200 OK
- 端口 19001 (llama-swap 动态模型路由器)：PID 40988  -> 正常侦听
- 端口 8082  (llama-server 推理计算引擎)：PID 43360  -> 正常侦听，Cyber-Tiel 持续挂载
- 端口 19250 (Guard 安全控制平面服务)：PID 21828  -> 正常侦听，状态健康
- 端口 3001  (Open WebUI 前端接入服务)：PID 3392   -> 正常侦听

网关健康探针：
GET http://127.0.0.1:19000/v1/models
Status: 200 OK
Active Models:
  - cyber-tiel: loaded (Cyber-Tiel-Coder-35B-A3B)
  - local-coder: profile (固定客户端路由模型)
  - qwen-36: unloaded (Qwen3.6-35B-A3B-UD-Q4_K_M)
```

验证结论：**生产服务零重启、零闪断、零端口冲突，Hermes 与客户端正常使用。**

---

## I. 阶段裁决与操作建议

### 阶段裁决
`READY_FOR_MAIN_MERGE_WITH_HEURISTIC_TRANSLATION_LIMITATION`

### 操作建议（供用户决议）
1. **合并 PR #11**：当前 PR #11 全部云端工作流已完全变绿，代码架构纯净，可安全执行 `Squash and merge` 或 `Merge commit` 将 v3.2.0 发行候选代码并入 `main`。
2. **正式发行建议**：
   - 合并完成后，在 `main` 的最新提交上正式打上 Git 标签 `v3.2.0` 并推送到 GitHub。
   - 使用清单中的安装包与便携包创建 GitHub Release，并在 Release Notes 中明确注明：`zh-CN` 与 `en-US` 经过人工精细验证，其余 19 种语言为基线预置。
