# 本地模型调度台 (Local Model Conductor / LMC)

> **One API. Safe Model Switching.**  
> 专为 Windows 开发者打造的本地大模型 Runtime 管理、GPU 显存安全调度与统一 API Gateway 平台。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg)](https://microsoft.com/windows)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

---

## 🌟 核心亮点与产品优势

1. **唯一固定 API 终结点 (Unified API Gateway)**:
   客户端（Open WebUI、Hermes Agent、Cline、Codex、VS Code 等）永远固定连接：
   ```
   http://127.0.0.1:19000/v1
   ```
   切换模型时，客户端**无需修改端口、无需重启连接、无需更改环境变量**。

2. **唯一固定模型标识 (Fixed Model ID)**:
   所有客户端固定使用统一模型名：
   ```json
   "model": "local-coder"
   ```
   后台透明路由至当前调度的实际本地大模型（如 Cyber-Tiel 35B、Qwen3.6 35B、Inkling-2 等）。

3. **独占安全控制层 (LocalLLMManager.Guard)**:
   - **请求优雅排空 (In-Flight Request Draining)**：切换模型前自动暂停推理并排空正在流式传输的 SSE 请求，零截断。
   - **先进先出公平队列 (FIFO Queue)**：切换期间新到达的推理请求进入排队序列，切换完成后自动恢复处理。
   - **全自动故障回滚 (Automatic Rollback)**：若新模型启动超时、显存超限或健康检查失败，Guard 毫秒级自动回退至上次已知的良好模型（Last Known Good Profile），推理服务瞬间自愈。
   - **显存与提交限制门禁 (GPU / RAM / Commit Gate)**：切换前严密核算显存（VRAM）、物理内存（RAM）与 Commit 预算，防范 GPU OOM 崩溃。
   - **现役外部进程保护 (External Process Protection)**：自动识别并保护外部进程（如独立启动的 `llama-server.exe` 或其他 Codex 控制的模型），进入只读安全观察模式，杜绝抢占干扰。

4. **零环境依赖，自包含极速体验**:
   - 基于 .NET 10 Self-Contained 与 Native AOT 机器码编译，单机独立运行。
   - 用户**无需安装 .NET Runtime**、无需配置 Python 环境、无需安装 VC++ 运行库。

---

## 🚀 三大安装与使用方式

### 方式 A: PowerShell 一键安装（推荐普通用户）
打开 Windows PowerShell（无需管理员权限），运行以下命令：
```powershell
irm https://raw.githubusercontent.com/oliverhe202018-ctrl/Local-Model-Conductor/main/install.ps1 | iex
```
*注：脚本自动从 GitHub Releases 获取最新稳定版本，强制比对 SHA-256 校验和后自动启动安装。*

### 方式 B: 下载 Windows 官方安装包
前往 [Releases 页面](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases) 下载：
`LocalModelConductor-Setup-win-x64.exe`
- 遵循 Per-User 规范，无需 UAC 提权。
- 升级时自动继承配置与数据库，卸载时**默认绝对保留 GGUF 模型与历史数据**。

### 方式 C: 绿色便携版 (Portable ZIP)
下载 `LocalModelConductor-Portable-win-x64.zip`：
- 解压到任意目录（推荐 `D:\LocalModelConductor`）。
- 双击 `LlamaCppWindowsManager.exe` 即可启动，解压即用。

---

## 🛠️ 架构速览

```
[客户端: Open WebUI / Codex / Hermes]
                   │
                   ▼  (固定: http://127.0.0.1:19000/v1 | 模型: local-coder)
    ┌───────────────────────────────┐
    │  llama-swap v275 数据面网关   │
    └──────────────┬────────────────┘
                   │  (受控管理通道)
    ┌──────────────┴────────────────┐
    │    LocalLLMManager.Guard      │ ◄──── Windows Named Pipe 访问控制
    │  (独占凭据 / 资源门禁 / 回滚) │
    └──────┬─────────────────┬──────┘
           │                 │
           ▼                 ▼
[WPF 桌面控制台]     [llwmctl Native CLI]
```

---

## 💻 命令行快速操作 (`llwmctl`)

`llwmctl.exe` 采用原生 Native AOT 编译，响应毫秒级：

```powershell
# 1. 查询网关与 GPU 显存门禁状态
llwmctl gateway status

# 2. 检查网关健康度
llwmctl gateway health

# 3. 安全切换推理模型
llwmctl switch qwen3.6-coder-35b

# 4. 故障时手动触发基线回滚
llwmctl rollback

# 5. 查看审计日志
llwmctl logs audit
```

---

## 📄 开源许可证

本项目核心代码基于 [MIT 许可证](LICENSE) 发布。第三方依赖声明与组件清单详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 [sbom.spdx.json](sbom.spdx.json)。
