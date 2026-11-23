# Phase 4D.3 — Local Model Conductor 版本主线整合与统一发行候选（Unified RC）验收报告

**执行模式**: `EXECUTE_ISOLATED_INTEGRATION_NO_RELEASE`  
**正式产品名称**: Local Model Conductor（本地模型调度台）  
**正式 GitHub 仓库**: `https://github.com/oliverhe202018-ctrl/Local-Model-Conductor`  
**集成工作区分支**: `feature/phase4d3-unified-release-candidate`  
**基线 Commit**: `367ab4e27fa0f8933997ca93eccd9c826a0c5424` (PR #9 HEAD, CI 验证通过)  
**最终集成 Commit**: `e32d8ad3b6ee979601d2ec7570ae9a98efb04ee4`  
**报告完成时间**: 2026-11-24  

---

## 最终裁决：`UNIFIED_RC_READY_WITH_LIMITATIONS`

> [!IMPORTANT]
> **裁决理由**:
> 1. **主线整合完全成功**：PR #9 严格 CI 门禁底座与 Phase 4D-2 的 21 种语言 JSON 包、双语安装包及品牌更新源修复完成无损嫁接。
> 2. **全量自动化测试 100% 通过**：在统一集成 Commit `e32d8ad` 下，单次完整执行全部 3 个测试套件，**1,549 PASS，0 FAIL，0 SKIP**（Core: 1404 PASS；UI: 141 PASS；Guard: 4 PASS）。
> 3. **零外部依赖 Self-Contained 单文件打包成功**：生成 NSIS 安装包（83.86 MB）与便携版 ZIP（115.73 MB），通过打包前 13 项完整性门禁，无任何敏感信息泄露。
> 4. **生产环境零重启、零掉线**：Ports 19000、19001、8082、19250、3001 全程持续健康，PID 保持不变，Hermes 与 Open WebUI 访问不受影响。
> 
> **限制清单 (Limitations)**:
> 1. **安装器隔离环境测试限制**：本地宿主机无可用 Windows Sandbox 权限，安装与卸载逻辑通过对 `installer/LocalModelConductor.nsi` 的严密代码级静态审查验证，未在干净沙箱中执行端到端物理安装。
> 2. **多语言包质量覆盖限制**：仅中文 `zh-CN` 与英文 `en-US` 经过真实 UI 渲染与截图验证（21 种语言均包含 1990 个结构对齐的键），其余 19 种语言为基准翻译，后续可通过社群持续校阅。

---

## 一、关键问题逐项回答（13 项详尽答卷）

### 1. 统一集成分支的基线 Commit 是什么？PR #9 与 4D-2 分别移植了哪些成果？

- **基线 Commit**: `367ab4e27fa0f8933997ca93eccd9c826a0c5424`（即 PR #9 HEAD，分支 `feature/phase4b-5-publication-hardening`）。该提交已在 GitHub Actions 上通过完整 CI 门禁（1536 测试全绿、CodeQL、Dependency Review）。
- **保留自 PR #9 的成果**:
  - 全套 GitHub Actions 严格 CI 流水线（`.github/workflows/` 包括 Go、Python 运行环境与 Guard 测试 fixture）。
  - `GuardControlCoordinator.Switching.cs` 模块化解耦架构。
  - `tests/fixtures/mock_fault_server.py` 与动态测试路径修复。
  - `scripts/build-distribution.ps1` 中的打包前完整性门禁（13 项刚性文件校验）。
  - `scripts/package-audit-policy.ps1` 对测试运行框架遗留包的白名单安全处理。
  - `installer/LocalModelConductor.nsi` 的参数化路径与零管理员权限设计。
- **自 Phase 4D-2（`0f187207`）移植的成果**:
  - 21 种语言资源包（`src/LocalLlmConsole.App/Localization/Strings.*.json`），每包 1,990 个键。
  - `Loc.cs` 多语言核心引擎（系统语言自动识别、RTL 布局、多级回退）。
  - WPF 运行时即时切换逻辑（`LanguageCombo_SelectionChanged` → `ApplyLocalizedXamlStrings()` → `RefreshCurrentPage()`）。
  - Guard 运维中心全面国际化（`GuardOperationsCenterPage.cs`、`.Cards.cs`、`.Logs.cs`、`GuardOperationsCenterController.cs`）。
  - 用户界面语言设置持久化到本地 SQLite 数据库（`MainWindow.SettingsPersistence.cs`）。
  - 品牌与更新源修复（`AppUpdateService.cs` 迁移至 `oliverhe202018-ctrl/Local-Model-Conductor`，并保持对旧版 URL 的向后兼容）。
  - 双语 NSIS 安装向导（`installer/LocalModelConductor.nsi` 包含中文与英文 MUI 语言页）。
  - 截图与品牌兼容性测试套件（`LocalizationScreenshotTests.cs`、`AppUpdateBrandingAndCompatibility.Tests.cs`）。

---

### 2. 为什么选择该集成方案？如何证明没有引入回归破坏 PR #9 的 CI 门禁？

- **方案选型理由**:
  Git DAG 审计表明，本地 `main` 分支（包含 4D-2 提交 `0f187207`）与 PR #9（`367ab4e`）均分叉自公共祖先 `17c09a2`。PR #9 拥有经过云端验证的完整 CI/CD 与构建脚本，但缺少多语言和新更新源；而本地 `main` 拥有完整多语言，但在测试结构和构建脚本上有历史覆盖。
  因此，**以 PR #9 HEAD 为坚实底座，按功能模块选择性移植多语言、品牌和测试代码**，是风险最低、架构最清晰的最佳方案。
- **零破坏证明**:
  1. 保留 PR #9 的全部 CI 脚本（`ci.yml`、`build-distribution.ps1` 等），未被旧版本覆盖。
  2. 在集成工作区上完整运行核心测试 `LocalLlmConsole.Tests`（1404 测试）、UI 测试 `LocalLlmConsole.UiTests`（141 测试）、Guard 测试 `LocalLLMManager.Guard.Tests`（4 测试），**1549/1549 全部通过**。
  3. 执行 `scripts/build-distribution.ps1`，其内置的 13 项打包完整性门禁全部一次性亮绿通过。

---

### 3. 软件的正式命名、UI 标题、安装包名称、托盘名称、注册表项是否完全统一？

所有用户可见层与元数据已严格统一，彻底消除了历史名称分歧：

| 标识项 | 统一取值 | 依据代码/文件 |
|--------|---------|-------------|
| **产品英文名** | `Local Model Conductor` | `AppDisplayName` in `MainWindow.xaml.cs`, `Strings.en.json` |
| **产品中文名** | `本地模型调度台` | `App.Title` in `Strings.zh.json`, `PRODUCT_NAME_ZH` in NSIS |
| **主窗口标题** | `Local Model Conductor v3.2.0` / `本地模型调度台` | `MainWindow.xaml`, `MainWindow.Localization.cs` |
| **程序集标题** | `Local Model Conductor` | `LocalLlmConsole.App.csproj` (`<AssemblyTitle>`, `<Product>`) |
| **系统托盘** | `Local Model Conductor` / `Loc.T("Tray.*")` | `TrayIconHost.cs`, `MainWindow.TrayProfiles.cs` |
| **NSIS 安装包名** | `LocalModelConductor-Setup-win-x64.exe` | `build-distribution.ps1`, `LocalModelConductor.nsi` |
| **便携版压缩包** | `LocalModelConductor-Portable-win-x64.zip` | `build-distribution.ps1` |
| **开始菜单快捷方式** | `$SMPROGRAMS\Local Model Conductor\` | `LocalModelConductor.nsi` |
| **桌面快捷方式** | `$DESKTOP\Local Model Conductor.lnk` | `LocalModelConductor.nsi` |
| **卸载注册表项** | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalModelConductor` | `LocalModelConductor.nsi` |
| **应用注册表项** | `HKCU\Software\LocalModelConductor` | `LocalModelConductor.nsi` |
| **卸载控制面板显示** | `本地模型调度台 (Local Model Conductor)` | `LocalModelConductor.nsi` (`DisplayName`) |

---

### 4. 多语言支持的实际状态：中英文是否均具备 100% 对应？命名占位符是否存在不一致？其它 19 种语言包的完整性与限制是什么？

- **中英文对齐度**:
  - 英文基准包（`Strings.en.json`）：**1,990 个资源键**。
  - 中文包（`Strings.zh.json`）：**1,990 个资源键**。
  - 缺失键数量：**0**（双向完全对齐，100% 覆盖率）。
- **占位符一致性核验**:
  通过自动化脚本对所有 1,990 个键的正则占位符提取分析：
  **占位符不匹配数：0**（所有 `{0}`, `{1}` 等位置参数在中英文两包中完全一一对应，无任何格式化崩溃风险）。
- **其它 19 种语言包的状态与限制**:
  - 全部 21 种语言 JSON 文件均具备合规 JSON 语法且均包含完整的 1,990 个键。
  - **限制明确说明**：除中文与英文通过人工语义校对及 UI 渲染测试外，其余 19 种语言包是由上游基准及机翻工具生成的预置包。界面文字结构完整不会报错，但翻译地道性需要后续语言专家或开源社区进一步润色。

---

### 5. 全量自动化测试的执行环境、Commit、通过数、失败数、跳过数分别是什么？如果有失败项，根因是什么？

- **执行环境**: Windows 11 x64, .NET SDK 10.0.404, xUnit.net v3 In-Process Runner v4.0.3
- **测试执行 Commit**: `e32d8ad3b6ee979601d2ec7570ae9a98efb04ee4`（统一集成分支，单一连续测试流程）
- **测试统计全景表**:

| 测试套件 | 对应程序集 | 总数 | 通过 (PASS) | 失败 (FAIL) | 跳过 (SKIP) | 耗时 |
|---------|-----------|------|------------|------------|------------|------|
| **Core 测试** | `LocalLlmConsole.Tests.exe` | 1,404 | **1,404** | **0** | 0 | 76.38s |
| **UI 测试** | `LocalLlmConsole.UiTests.exe` | 141 | **141** | **0** | 0 | 63.76s |
| **Guard 安全测试** | `LocalLLMManager.Guard.Tests.exe` | 4 | **4** | **0** | 0 | 84.85s |
| **总计** | **全部 3 个测试工程** | **1,549** | **1,549 (100%)** | **0** | **0** | **224.99s** |

- **失败项根因与修复过程**:
  在集成初次跑测时曾出现 5 项断言失败，均已定位并彻底修复：
  1. `MainWindowShell.Tests`: 断言硬编码版本 `"v3.0.0"`，而当前已演进至 `"v3.2.0"`。修复：更新断言为 `"v3.2.0"`。
  2. `ProjectDeclaresCurrentReleaseMetadata`: 断言检查硬编码版本 `"3.0.0"`。修复：更新断言与项目元数据 `"3.2.0"` 对齐。
  3. `ThirdPartyNoticesMatchDeclaredDatabaseDependencies`: 在覆盖移植时丢失了上游 `Microsoft.Data.Sqlite 10.0.15` 声明。修复：从 PR #9 还原 `THIRD-PARTY-NOTICES.md`。
  4. `ReleaseDocsAndScriptsUseLaunchBranding` & `InstallerKeepsUserDataUnlessExplicitlyRequested`: 覆盖移植时将完整的 PR #9 README 覆盖为旧版。修复：从 PR #9 还原完整的 `README.md`。
  修复后全套 1,549 项测试一次性全绿。

---

### 6. PR #9 与 4D-2 的安装包大小差异根因是什么？统一 RC 采用什么发布配置？

- **差异根因深度分析**:
  - **PR #9 采用 Self-Contained 单文件发布**：
    执行 `dotnet publish ... --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`。
    将完整的 .NET 10 Runtime、WPF/WindowsDesktop 框架、JIT 编译器与核心库打包进 `LlamaCppWindowsManager.exe`（~80 MB）与 `LocalLLMManager.Guard.Service.exe`（~40 MB）。打包产物安装包为 ~83.86 MB，便携包为 ~115.68 MB。
  - **4D-2 采用 Framework-Dependent 依赖框架发布**：
    未打包 .NET 运行时，主程序仅输出 9 MB 的 DLL 与 172 KB 的 AppHost，额外拷贝了外部 `caddy.exe` 与 `llama-swap.exe`。安装包为 35.62 MB，便携包为 44.85 MB。
- **统一 RC 发布方案选型与技术依据**:
  **正式采纳：Self-Contained 单文件独立分发方案**。
  **技术依据**:
  1. **开箱即用，免运维依赖**：.NET 10 是当前前沿的运行时版本，绝大多数用户的 Windows 系统未预装 .NET 10 桌面运行时。Framework-dependent 会在目标机器直接弹出运行时缺失错误框，造成发布事故。
  2. **生产环境隔离一致性**：Self-Contained 保证应用运行在固定经过验证的 BCL 与 GC 行为下，不受宿主机 .NET 版本升级或损坏影响。
  3. **符合 PR #9 云端 CI 验收门禁要求**。

---

### 7. 生成的 Release Manifest 包含哪些文件？每个文件的 SHA-256 和大小分别是多少？

产物生成目录：`D:\project\本地模型计算\phase4d3_integration\dist\release`

| 产物文件名 | 文件类型 | 大小 (字节 / MB) | SHA-256 校验和 |
|-----------|---------|-----------------|----------------|
| **`LocalModelConductor-Setup-win-x64.exe`** | NSIS 安装程序 (Self-Contained) | 87,937,963 B (83.86 MB) | `e30fd49a5a8e9c739af2fd42c872b002c76c63d4b3960e10019818bdc2a68bb0` |
| **`LocalModelConductor-Portable-win-x64.zip`** | 便携版 ZIP 压缩包 (Self-Contained) | 121,355,850 B (115.73 MB) | `fb800af2bc4672fa6e41b2c8058a07ea165451f499d8c6f8f55768c756503eaf` |

清单文件 `release-manifest.json` 与 `SHA256SUMS.txt` 已同步生成于产物目录。

---

### 8. 是否包含生产机敏感配置？如何证明打包的干净性？

- **打包目录完全纯净证明**:
  经对 `dist\LlamaCppWindowsManager-win-x64` 全目录文件枚举审查：
  - **零敏感数据**：无 `data/` 目录，无 `*.sqlite`、`*.db`、`*.wal`、`*.shm` 数据库文件。
  - **零机密凭据**：无 `control.json`，无任何 Bearer Token、API Key 或明文密码。
  - **零开发副产物**：构建脚本第 1.6 步自动清洗了全部 `*.pdb`、`web.config`、`*.publish.lock.json` 与 `*.endpoints.json`。
  - **打包内容仅包含**：`LlamaCppWindowsManager.exe` (+.sha256)、`llwmctl.exe` (+.sha256)、`Guard/LocalLLMManager.Guard.Service.exe` (+.sha256)、文档、许可证与 `sbom.spdx.json`。

---

### 9. 安装包在全新安装、覆盖升级、静默卸载场景下的表现与数据保留机制为何？是否通过真实测试或完成代码级严密审计？

由于本地无法运行 Windows Sandbox 容器，依据规划开展了严密的代码级静态审计（审计对象：`installer/LocalModelConductor.nsi`）：

1. **全新安装场景**:
   - `RequestExecutionLevel user` 确立单用户上下文，写入 `$LOCALAPPDATA\Programs\LocalModelConductor`，全程免 UAC 弹窗提权。
   - 创建骨架目录：`CreateDirectory "$INSTDIR\data\models"`、`runtimes`、`state`、`logs`。首次启动由软件程序自动生成全新的空数据库与默认设置，绝不引入开发机遗留历史。
2. **覆盖升级场景**:
   - 安装前通过 `taskkill /F /IM ... /FI "STATUS eq RUNNING"` 安全停止已在运行的旧版进程。
   - `CreateDirectory` 具有幂等性，已存在的 `$INSTDIR\data\` 目录及内部的模型文件、自定义配置文件、历史数据库**完全保留，不会被覆盖或清空**。
3. **卸载与数据保留场景**:
   - 交互模式下通过对话框明确提示：`[是] 彻底清空全部数据 / [否 (推荐)] 保留模型与配置`，默认焦点置于“保留（否）”。
   - 静默卸载时通过 `IfSilent KeepData` **强制直达保留数据分支**，杜绝自动化流水线或脚本误删用户数十GB GGUF 模型资产。

---

### 10. 生产环境是否在整个集成期间保持 100% 连续可用、零重启、零掉线？

**确认全部生产组件零干扰，全程持续稳定**：

| 组件 / 进程名 | 监听端口 | 进程 PID | 运行状态 | 连续运行启动时间 |
|--------------|---------|---------|---------|----------------|
| **Caddy 网关** | `19000` | `41032` | ✅ 正常监听 | 2026/11/23 19:34:19 (未重启) |
| **llama-swap** | `19001` | `40988` | ✅ 正常监听 | 2026/11/23 20:35:40 (未重启) |
| **llama-server (Cyber-Tiel 35B)** | `8082` | `43360` | ✅ 正常服务 (~15.5GB VRAM) | 2026/11/23 22:24:44 (未重启) |
| **Guard 安全控制服务** | `19250` | `21828` | ✅ 正常监听 | 2026/11/23 20:52:17 (未重启) |
| **Open WebUI (Python)** | `3001` | `3392` | ✅ 正常监听 | 2026/11/23 20:16:10 (未重启) |

Hermes 访问（`http://127.0.0.1:19000/v1` 模型 `local-coder`）全程无任何中断。

---

### 11. UI API Key 与 Caddy Bearer Token 的关系是否已向后兼容并文档化？

已在代码与架构文档中明确厘清解耦：
- **Caddy Bearer Token**：专用于外部客户端（IDE、Hermes、Open WebUI、Codex）向 `http://127.0.0.1:19000/v1` 发送推理请求时的 HTTP Bearer 鉴权，由 Caddyfile 统一定义与防护。
- **UI API Key / Named Pipe**：WPF 控制台与 Guard 控制服务之间通过本地管道 IPC（`LocalLLMManager.Guard.Control.v1`）通信，不需要 Bearer Token；UI 运维日志模块已内置自动脱敏逻辑，绝不向界面渲染或泄露外部客户端的 Bearer Token。
- 两者语义彻底隔离，互不混淆。

---

### 12. 本地旧 Tag（v3.2.0）的现状为何？当前统一 RC 对应的 Commit 是否需要推标签？

- **旧 Tag 现状**: 本地 Tag `v3.2.0` 目前指向主仓库 `main` 分支的历史提交 `0f187207`。
- **遵守操作规范**:
  1. **本阶段绝不删除、移动或覆盖本地已有 Tag**。
  2. **本阶段绝不向远程仓库推送任何 `v*` Tag，绝不向 `main` 分支执行推送或合并**。
  3. 当前统一 RC 成果已完整锁定在集成工作区分支 `feature/phase4d3-unified-release-candidate`（Commit `e32d8ad`）。待后续获得正式发布授权后，方可在主线上统一步进并打 Tag 发布。

---

### 13. 最终验收裁决与结论

**最终裁决**: `UNIFIED_RC_READY_WITH_LIMITATIONS`

统一发行候选版本已在隔离环境中完成全量整合、全景测试与独立打包，产物完整有效，生产环境完全零干扰。随时可根据用户决策推进后续发布流程。
