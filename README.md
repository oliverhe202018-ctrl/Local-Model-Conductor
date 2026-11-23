# Local Model Conductor (LMC)

> **One API. Safe Model Switching.**<br>
> An open-source, unofficial community project providing a professional Windows desktop platform for local LLM runtime management, GPU resource scheduling, zero-downtime safe model switching, and unified API gateway control.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg)](https://microsoft.com/windows)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

[中文文档 (README.zh-CN.md)](README.zh-CN.md)

---

## 🌟 Key Features

1. **Unified API Gateway Endpoint**:
   Your IDEs, Open WebUI, Codex, Hermes Agent, and code assistants connect to one permanent URL:
   ```
   http://127.0.0.1:19000/v1
   ```
   Switching models never requires changing ports, updating URLs, or restarting clients.

2. **Fixed Model Identifier**:
   Clients request one constant model ID:
   ```json
   "model": "local-coder"
   ```
   The backend transparently routes traffic to the currently active GGUF model (e.g. Cyber-Tiel, Qwen3.6, Inkling-2).

3. **Isolated Safety Control Plane (`LocalLLMManager.Guard`)**:
   - **In-Flight Request Draining**: Gracefully drains active streaming SSE requests before model unloading without truncating tokens.
   - **FIFO Request Queuing**: Holds incoming requests in an orderly queue during model switch and dispatches them upon completion.
   - **Automatic Failure Rollback**: Restores the last-known-good profile in milliseconds if the new model fails to load, exceeds memory limits, or fails health checks.
   - **Pre-Flight Safety Gate**: Evaluates GPU VRAM, physical RAM, commit limits, and port availability before allowing a switch.
   - **External Process Protection**: Detects and shields external processes (e.g., active unmanaged `llama-server.exe` or Codex sessions) by enforcing a read-only observation mode.

4. **Self-Contained & Zero Dependencies**:
   - Built on .NET 10 Self-Contained and Native AOT compilation.
   - No external .NET runtime, Python runtime, or VC++ redistributable installation required.

---

## 🚀 Installation & Getting Started

> [!NOTE]
> **Release Status:** The repository currently provides public open-source code and automated build pipelines. Official pre-built installer packages will be **available after the first official release (v3.0.0)**.

### Method 1: PowerShell One-Click Install (Recommended)
Run in Windows PowerShell (No Administrator elevation required):
```powershell
irm https://raw.githubusercontent.com/oliverhe202018-ctrl/Local-Model-Conductor/main/install.ps1 | iex
```

### Method 2: Official NSIS Windows Installer
Download `LocalModelConductor-Setup-win-x64.exe` from [GitHub Releases](https://github.com/oliverhe202018-ctrl/Local-Model-Conductor/releases).
- Installs to `%LOCALAPPDATA%\Programs\LocalModelConductor` (or `D:\LocalModelConductor`).
- Upgrades preserve user data seamlessly.
- Uninstallation strictly **keeps GGUF models, state, and databases by default**.

### Method 3: Portable ZIP (Green Edition)
Download `LocalModelConductor-Portable-win-x64.zip`:
- Extract anywhere and run `LlamaCppWindowsManager.exe`.

---

## 💻 CLI Quickstart (`llwmctl`)

The compiled Native AOT CLI provides sub-millisecond execution:

```powershell
# Query status and GPU resource gate
llwmctl gateway status

# Check gateway and facade health
llwmctl gateway health

# Safely switch active profile
llwmctl switch qwen3.6-coder-35b

# Trigger emergency rollback to last known good baseline
llwmctl rollback

# Inspect redacted security audit trail
llwmctl logs audit
```

---

## 📚 Documentation

- [DEVELOPMENT.md](docs/DEVELOPMENT.md) — Contributor, development, and testing guide.
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — System architecture and component boundaries.
- [CONTROL_API.md](docs/CONTROL_API.md) — Guard IPC and Control API specification.
- [INSTALLER.md](docs/INSTALLER.md) — Installation, packaging, and upgrade lifecycle.

---

## 🤝 Upstream & Acknowledgements

Local Model Conductor is a controlled fork and architectural evolution derived from:
- [alekk89/llama-cpp-windows-manager](https://github.com/alekk89/llama-cpp-windows-manager) (MIT License, Copyright (c) 2024 alekk89) — Desktop UI foundation, runtime management, and model asset catalog.
- [mostlygeek/llama-swap](https://github.com/mostlygeek/llama-swap) (MIT License, Copyright (c) mostlygeek) — Dynamic model swapping and unified proxy gateway.
- [ggerganov/llama.cpp](https://github.com/ggerganov/llama.cpp) (MIT License, Copyright (c) 2023-2025 Georgi Gerganov) — Core LLM inference engine.

---

## 📄 License

Local Model Conductor is released under the [MIT License](LICENSE). Third-party runtime and component notices are documented in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `sbom.spdx.json` (distributed with release packages).
