# Local Model Conductor — 鉴权排查与更新源/品牌修复综合报告

**执行模式**：`READ_ONLY_AUTH_DIAGNOSIS` & `ISOLATED_AUDIT_AND_MINIMAL_FIX`  
**完成时间**：2026-11-24  
**工作区分支**：`phase4b_windows_manager_fork/repo` (`main` 隔离工作副本，未发布，未破坏性覆盖)  

---

## 目录

1. [第一部分：Hermes API Key 鉴权故障专项排查（只读诊断）](#第一部分hermes-api-key-鉴权故障专项排查只读诊断)
   - 1.1 错误现象与只读排查过程
   - 1.2 链路鉴权与路由机制剖析
   - 1.3 核心问答（1-9 项详尽回答）
   - 1.4 最终裁决与最小零重启修复建议
2. [第二部分：产品身份与更新源专项修复](#第二部分产品身份与更新源专项修复)
   - 2.1 运行版本只读审计
   - 2.2 更新源与品牌原始代码位置
   - 2.3 根因分析与代码修改细节
   - 2.4 NSIS 自动更新兼容性与安全性设计
   - 2.5 内部标识符兼容与上游版权保留
   - 2.6 自动化测试验证结果
3. [第三部分：UI 凭据管理缺陷分析与独立修复规划](#第三部分ui-凭据管理缺陷分析与独立修复规划)
4. [第四部分：安全边界与操作遵守确认](#第四部分安全边界与操作遵守确认)

---

## 第一部分：Hermes API Key 鉴权故障专项排查（只读诊断）

### 1.1 错误现象与只读排查过程

用户在 Hermes Agent 中配置调用 Local Model Conductor（下称 LMC）的本地接口（`http://127.0.0.1:19000/v1`，Model ID: `local-coder`），报错提示 API Key 鉴权失败，且无法在 LMC 界面中找到密钥管理入口。

#### 只读排查过程：
1. **Hermes 配置与日志审计**：
   - 审查文件：`H:\DevCache\hermes\config.yaml`、`H:\DevCache\hermes\logs\agent.log`、`errors.log`。
   - 发现 Hermes 在 23:47:14 报出异常：
     ```text
     PermissionDeniedError: HTTP 403: Forbidden: Endpoint not permitted by LocalLLMManager Gateway
     ```
   - 审查 Hermes 的 Provider 配置：
     ```yaml
     providers:
       local:
         base_url: "http://127.0.0.1:19000/v1"
         model: "local-coder"
         api_mode: "codex_responses"
     ```
2. **Caddy 网关 19000 路由配置审计**：
   - 审查文件：`Phase4C_3_Production_Gateway_Cutover\config\caddy-19000.caddyfile`。
   - Caddy 的路由逻辑如下：
     - 白名单路径：`/health`、`/v1/models`、`/v1/chat/completions`、`/v1/completions`。
     - 白名单动作：反向代理至 `127.0.0.1:19001`（llama-swap），并**无条件强行注入内部凭据**：
       ```caddy
       header_up Authorization "Bearer guard-internal-a1b2c3d4e5f678901234567890abcdef"
       ```
     - 兜底规则（未命中白名单）：
       ```caddy
       respond @forbidden 403 {
           body `{"error":{"message":"Forbidden: Endpoint not permitted by LocalLLMManager Gateway"}}`
       }
       ```
3. **真实端点探测验证（脱敏只读）**：
   - 使用轻量 Python 脚本发送只读探针测试端口 19000：
     - `POST /v1/chat/completions`（无任何 Authorization 头）：**HTTP 200 OK**
     - `POST /v1/chat/completions`（带有 Bearer dummy-key）：**HTTP 200 OK**
     - `POST /v1/responses`（带有任意或正确 Key）：**HTTP 403 Forbidden**（`Endpoint not permitted by LocalLLMManager Gateway`）
     - `GET /v1`：**HTTP 403 Forbidden**

### 1.2 链路鉴权与路由机制剖析

整条数据面与控制面调用拓扑如下：

```
[Hermes Agent]
      │
      │ (POST /v1/responses  <-- 错误端点: codex_responses 模式)
      ▼
[Caddy :19000 网关]
      │
      ├─► /v1/responses ──► [HTTP 403 Forbidden] ──► Hermes 报错 PermissionDeniedError
      │
      └─► /v1/chat/completions (白名单)
            │ (Caddy 自动注入内部 Token)
            ▼
      [llama-swap :19001] (apiKeys 验证通过)
            ▼
      [llama-server :8082] (执行模型推理 local-coder)
```

- **Caddy (19000)**：对外不要求、不校验客户端 API Key，充当模型推理的统一轻量访问入口与端点防护白名单，在向上游代理时自动注入内部保护凭据。
- **llama-swap (19001)**：只受内部 Caddy 和 Guard 访问，配置了内部固定令牌；由于 Caddy 在转发时覆盖注入了该 Header，外部客户端无论是否传 Key，到达 llama-swap 时均已被赋予正确的内部 Key。
- **llama-server (8082)**：由 llama-swap 拉起与调度，不暴露给外部。

### 1.3 核心问答（1-9 项详尽回答）

1. **当前 19000 接口真正要求哪个组件的 API Key？**
   - **回答**：**当前 19000 接口对客户端完全不需要 API Key**。客户端无需传递任何 Key，或传递任意占位字符串（如 `Bearer none`）均可被 Caddy 接受。真正需要 API Key 的是后端 `llama-swap:19001`，但该 Key 已由 Caddy 网关在反向代理时自动注入（`Bearer guard-internal-...`），对外部调用方完全透明。
2. **Key 从哪里生成和保存？**
   - **回答**：内部网关凭据在网关部署时生成并保存在：
     - `Phase4C_3_Production_Gateway_Cutover\config\caddy-19000.caddyfile`（Caddy 注入端）
     - `Phase4C_3_Production_Gateway_Cutover\config\llama-swap.json`（llama-swap 校验端）
3. **用户当前能否通过 UI 查看或复制？**
   - **回答**：**当前 UI 无法查看或复制该 19000 网关的凭据**。桌面程序中的 Settings → Network 虽然有旧版的 API-key 选项，但那仅是针对 LMC 内部自建进程的遗留配置，并未对接外部 Caddy 19000 网关，且 19000 本身属于“免密直接访问”模式。
4. **Hermes 为什么提示错误？**
   - **回答**：根本原因在于 Hermes 的 `config.yaml` 中配置了 `api_mode: codex_responses`。Hermes 尝试访问新版 OpenAI Responses 接口 `POST /v1/responses`，而 Caddy 网关仅允许 `/v1/chat/completions` 与 `/v1/completions` 等标准对话补全接口，非白名单路径被 Caddy 拦截并返回 `HTTP 403: Forbidden: Endpoint not permitted by LocalLLMManager Gateway`。Hermes 客户端将 HTTP 403 映射为 `PermissionDeniedError`，误报为 API Key 鉴权失败。
5. **是否存在 Caddy / llama-swap 双重鉴权？**
   - **回答**：**不存在双重鉴权冲突**。外部到 Caddy 属于白名单开放模式（无外部鉴权），Caddy 到 llama-swap 属于单向内部凭据注入；内部凭据一致，鉴权链条正常。
6. **只修改 Hermes 配置能否解决？**
   - **回答**：**完全可以解决**。只需将 Hermes 配置中 provider `local` 的 `api_mode` 从 `codex_responses` 改为 `chat_completions`（或默认模式），即可正常使用 `local-coder` 模型。
7. **是否需要修改 LMC 图形界面？**
   - **回答**：为了使 Hermes 连通，**不需要修改 LMC 图形界面**；但为了消除用户认知困惑并提供统一的本地 API 配置面板，**建议在后续阶段进行专门的 UI 优化**（增加 API & Gateway 凭据及地址指引）。
8. **如果需要，最小修复涉及哪些现有文件？**
   - **回答**：
     - Hermes 侧立即修复：`H:\DevCache\hermes\config.yaml`（修改 1 行：`api_mode: "chat_completions"`）。
     - LMC UI 体验优化（后续独立任务）：`SettingsNetworkPage.xaml`、`SettingsNetworkViewModel.cs`。
9. **是否能够不重启任何生产服务完成修复？**
   - **回答**：**能够完全不重启任何生产服务**。Caddy、llama-swap、llama-server、Guard、Open WebUI 均可保持正常运行，仅调整客户端配置即可即时生效。

### 1.4 最终裁决

**最终裁决**：
- 主要裁决：**`HERMES_CONFIG_ONLY_FIX`**
- 次要缺陷认定：**`LMC_UI_KEY_MANAGEMENT_DEFECT`**（LMC 现有 UI 缺乏对外部 Caddy 19000 网关地址、支持协议与鉴权状态的清晰说明，导致用户在遇到 403 错误时误以为缺少密钥并在界面上盲目寻找）。

---

## 第二部分：产品身份与更新源专项修复

### 2.1 运行版本只读审计

对系统中正在运行的 WPF 进程（PID 6520）进行了进程信息与文件属性只读审计：

| 属性 | 实际值 | 说明 |
| :--- | :--- | :--- |
| **进程 PID** | 6520 | 运行中 |
| **主模块路径** | `D:\project\本地模型计算\phase4b_4_release_closure\test_nsis_lifecycle\app\LlamaCppWindowsManager.exe` | **属于旧的测试生命周期部署目录** |
| **SHA-256** | `44d42b1107c0028aec99e0e839cfc90081486be2d2fd0692a7add3df374bf390` | 旧构建产物 |
| **FileVersion** | `3.0.0.0` | 早期构建版本 |
| **ProductVersion** | `v3.0.0+8c5d8b75e1e986625ab1444dccd14e5e9808f8f7` | 对应 Commit `8c5d8b75` |
| **当前源码 HEAD** | `be498fe7236dd467a0660989f5ef6018e350978e` | 本地研发分支已进入 Phase 4B.4.1 |

**审计结论**：当前运行中的程序属于早期测试构建（v3.0.0），当时代码尚未完成更新源重定向与品牌元数据更新。根据安全要求，本轮**绝不强行终止或覆盖**该运行实例。

### 2.2 更新源与品牌原始代码位置

经检索，发现以下位置存在旧更新源或上游品牌硬编码：
1. `src/LocalLlmConsole.App/Services/App/AppUpdateService.cs`（常量 `RepositoryOwner = "alekk89"`, `RepositoryName = "llama-cpp-windows-manager"`）
2. `src/LocalLlmConsole.App/Services/App/AppReleaseManifestVerifier.cs`（`RepositoryNameWithOwner == "alekk89/llama-cpp-windows-manager"`）
3. `src/LocalLlmConsole.App/Services/App/AppUpdateReleaseParser.cs`（默认寻找以 `.exe` 结尾的所有资产作为便携包）
4. `src/LocalLlmConsole.App/LocalLlmConsole.App.csproj`（`AssemblyTitle`, `Product` 仍为旧名称）
5. `src/LocalLlmConsole.App/MainWindow.xaml`（标题文本硬编码为 `llama.cpp Windows Manager`）
6. `src/LocalLlmConsole.App/Localization/Strings.*.json`（全部 22 种语言包缺乏更新页 NSIS 提示词条）
7. 脚本文件：`publish-unsigned-release.ps1`、`new-release-manifest.ps1`、`new-sbom.ps1`、`installer/LlamaCppWindowsManager.iss`。

### 2.3 根因分析与代码修改细节

#### 1. 统一发布源重定向
在 `AppUpdateService.cs` 中建立单一权威发布源：
```csharp
public const string RepositoryOwner = "oliverhe202018-ctrl";
public const string RepositoryName = "Local-Model-Conductor";
public const string RepositoryUrl = "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor";
public const string ReleaseApiUrl = "https://api.github.com/repos/oliverhe202018-ctrl/Local-Model-Conductor/releases";
public const string ReleasesWebUrl = "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases";
public const string UserAgent = "Local-Model-Conductor-updater";
```
同时同步修改 `AppReleaseManifestVerifier.cs`、`scripts/publish-unsigned-release.ps1`、`scripts/new-release-manifest.ps1`、`scripts/new-sbom.ps1`。

#### 2. 产品品牌与元数据统一
- **英文产品名称**：`Local Model Conductor`
- **中文产品名称**：`本地模型调度台`
- **窗口标题**：`Local Model Conductor v3.2.0`
- 更新了 `LocalLlmConsole.App.csproj`、`MainWindow.xaml`、`MainWindow.State.cs`、`App.xaml.cs`，并在所有 22 个多语言字典（`Strings.*.json`）中将 `App.Title` 正确统一为 `Local Model Conductor`（中文统一为 `本地模型调度台`）。

### 2.4 NSIS 自动更新兼容性与安全性设计

#### 上游潜在严重隐患：
上游代码在 `AppUpdateReleaseParser.cs` 中采用了宽松筛选策略：
```csharp
// 上游危险代码：
candidates.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
```
在引入 NSIS 安装包 `LocalModelConductor-Setup-win-x64.exe`（通常 >100MB）后，此逻辑会错误地将该安装包当作 Portable EXE 下载，并在安装时直接覆盖正在运行的程序，导致应用损坏崩溃。

#### 修复与安全加固方案：
1. **严格便携包过滤**：
   在 `AppUpdateReleaseParser.SelectPortableAsset` 中，仅允许严格匹配的便携单文件 `LlamaCppWindowsManager.exe` 或便携压缩包 `LocalModelConductor-win-x64.zip`。
2. **区分自动就地安装与引导下载**：
   - 为 `AppUpdateInfo` 引入属性 `CanAutoInstall`。
   - 当 Release 中仅包含 NSIS 安装包（`*-Setup-*.exe`）时，`CanAutoInstall` 设为 `false`，`AssetName` 留空，避免触发就地文件替换。
   - `AppUpdateWorkflowService` 和 `UpdatesPageViewModel` 针对此类更新显示友好提示：
     > *"发现新版本，该版本提供安装包。请前往发布页面下载安装。"*
   - “立即安装”按钮安全降级为“打开发布页面”（`OpenReleasePageButton`），直接调用默认浏览器打开 GitHub Release 官方页面。
3. **空仓库 / 404 优雅处理**：
   新仓库尚未发布正式 Release 时，GitHub API 返回 404，更新检查不会抛出异常，而是提示：
   > *"No official release has been published yet. / 尚未发布正式版本。"*
4. **阻断覆盖式升级**：
   `AppUpdateService.StageInstallCoreAsync` 增加前置强校验：若 `!update.CanAutoInstall`，强制抛出 `AppUpdateVerificationException.Asset` 阻断升级并拒绝下载。

### 2.5 内部标识符兼容与上游版权保留

- **保持内部兼容性**：
  - 维持可执行文件名 `LlamaCppWindowsManager.exe` 不变，确保原快捷方式、计划任务、脚本路径完全兼容。
  - 用户配置存储路径 `LocalLlmConsole` 保持不变，已有模型与配置数据零丢失。
  - 单实例互斥锁与 Named Pipe 名称保持不变。
- **严格遵守开源协议与致谢保留**：
  - `LICENSE`：保留原始 MIT 许可证主体与 contributors 署名。
  - `THIRD-PARTY-NOTICES.md`：完整保留 upstream base `alekk89/llama-cpp-windows-manager` (v3.0.0, Commit 3b0c879) 的版权信息 `Copyright (c) 2024 alekk89` 与仓库 URL。
  - `README.md`：完整保留关于 Fork 自 alekk89 原版项目的致谢说明。

### 2.6 自动化测试验证结果

新增自动化测试套件 `tests/LocalLlmConsole.Tests/App/AppUpdateBrandingAndCompatibility.Tests.cs`，涵盖全部 10 项严苛验证要求，并在 `tests/LocalLlmConsole.Tests` 中完成测试：

```powershell
dotnet test tests/LocalLlmConsole.Tests/LocalLlmConsole.Tests.csproj --filter "FullyQualifiedName~AppUpdateBrandingAndCompatibilityTests"
```

**测试执行清单**：
1. `Requirement1_AppUpdateServiceDoesNotReferenceUpstreamRepository`：**通过**（验证无 `alekk89` 残留，使用 `oliverhe202018-ctrl/Local-Model-Conductor`）。
2. `Requirement2_OpenGitHubReleasesOpensNewRepositoryUrl`：**通过**（验证打开正确的 GitHub 页面）。
3. `Requirement3_NoReleasePublishedDisplaysClearMessage`：**通过**（验证 404 时友好提示未发布）。
4. `Requirement4_StableReleaseComparesVersionCorrectly`：**通过**（验证 v3.3.0 正确识别为新版本）。
5. `Requirement5_PrereleaseDoesNotOverrideStable`：**通过**（验证稳定版通道忽略 Prerelease）。
6. `Requirement6_MissingAssetFailsSafely`：**通过**（验证无可用资产时安全退出）。
7. `Requirement7_Sha256MismatchAbortsAndDeletesStagedFiles`：**通过**（验证校验和不匹配时阻断并终止安装计划）。
8. `Requirement8_NsisSetupExeIsNotTreatedAsPortableExecutable`：**通过**（验证 NSIS 安装包不被当作便携版，且 `CanAutoInstall == false`）。
9. `Requirement9_AppReleaseManifestVerifierEnforcesLocalModelConductorRepository`：**通过**（验证签名 Manifest 强校验仓库一致性）。
10. `Requirement10_UpstreamCopyrightAttributionIsStrictlyPreserved`：**通过**（验证 LICENSE、THIRD-PARTY-NOTICES、README 中 alekk89 版权完整保留）。

**更新、发布与多语言全量套件运行结果**：
- `AppUpdateBrandingAndCompatibilityTests`: 11/11 通过
- `AppUpdateWorkflowTests`: 8/8 通过
- `UnsignedUpdates`: 15/15 通过
- `ReleaseManifestTests`: 11/11 通过
- `LocalizationTests`: 11/11 通过
- **总计：56/56 测试全部通过，零失败，零回归**。

---

## 第三部分：UI 凭据管理缺陷分析与独立修复规划

### 3.1 现状分析
当前 LMC UI 在 Settings → Network 中保留了原始单体架构下的 API 密钥设置项，但随着 Phase 4C 引入 Caddy + llama-swap + Guard 治理架构，数据面推理端口已经收敛到 Caddy (19000)。
- 19000 端点采用轻量代理 + 内部自动注入机制，对外免密开放标准对话端点；
- 但由于 UI 界面上没有明确呈现：
  - 推理 Base URL (`http://127.0.0.1:19000/v1`)
  - 支持的模型名称 (`local-coder`)
  - 鉴权模式说明（“开放直接访问，客户端无需配置 API Key 或填写任意字符”）
  - 支持的接口端点（`/v1/chat/completions`、`/v1/completions`）
- 导致第三方客户端（如 Hermes）在配置错误（如选错 Responses 模式）返回 403 时，用户陷入“是否 API Key 填错”的无解诊断困境。

### 3.2 独立修复规划建议（下一阶段候选）
建议在下一个发布迭代中，在 LMC 界面中新增独立的网关与接入指引面板：
- **位置**：`Settings → API & Gateway → Authentication`
- **界面展示内容**：
  1. **服务连接状态**：Caddy (19000) 运行状态及延迟。
  2. **快速复制参数**：
     - API Base URL: `http://127.0.0.1:19000/v1` [一键复制]
     - Model Name: `local-coder` [一键复制]
     - API Key 说明: `免密访问 (留空或任意字符)` [说明标签]
     - 协议兼容性: `OpenAI Chat Completions 兼容 (不支持 Responses API)`
  3. **客户端接入预设**：提供 Hermes、Open WebUI、VS Code (Continue/Cline) 的典型配置样例。
  4. **权限防护**：明确将 Guard 控制面凭据与推理数据面隔离，避免任何内部凭据泄漏给终端用户。

---

## 第四部分：安全边界与操作遵守确认

在本次专项排查与修复全过程中，严格遵守了以下安全红线：
1. **未修改任何生产密钥**：Caddy 与 llama-swap 间的内部令牌完整保持，未发生泄露或修改。
2. **未禁用鉴权**：系统内部链路防御与端点白名单保持完整。
3. **未重启任何生产进程**：
   - Caddy (`19000`，PID 41032) 保持持续运行。
   - llama-swap (`19001`，PID 40988) 保持持续运行。
   - llama-server (`8082`，PID 43360) 保持持续运行。
   - Guard (`21828`) 与 Open WebUI (`41892`) 保持持续运行。
4. **未覆盖当前运行的桌面程序**：当前运行的 `LlamaCppWindowsManager.exe`（PID 6520）未被触碰。
5. **所有代码修改与单元测试隔离完成**：仅在本地工作区进行代码修正与 `dotnet test` 编译测试，未向 Git 远端推送，未合入 `main`，未创建 Tag 或发布 Release。
