# Local Model Conductor — System Architecture

```mermaid
flowchart TD
    subgraph CLIENTS["外部客户端生态"]
        UI_Client["Open WebUI / Web Client"]
        Dev_Tool["VS Code / JetBrains / Cline"]
        Agent["Hermes Agent / Autonomous Agent"]
    end

    subgraph DATA_PLANE["数据面 (llama-swap v275)"]
        Gateway["Fixed API Facade: http://127.0.0.1:19000/v1<br/>Fixed Model ID: local-coder"]
        ModelQueue["FIFO Request Queue & In-Flight Draining"]
        LlamaServer["llama-server (llama.cpp engine)"]
        Gateway --> ModelQueue
        ModelQueue --> LlamaServer
    end

    subgraph CONTROL_PLANE["控制面 (LocalLLMManager.Guard)"]
        NamedPipe["Windows ACL Named Pipe<br/>\\\\.\\pipe\\LocalModelConductor.Guard.*"]
        SafetyGate["Pre-Flight ResourceSafetyGate<br/>- VRAM / RAM / Commit limits<br/>- External Process Shield"]
        RollbackMgr["Rollback & Health Monitor<br/>- Automatic rollback to LastKnownGoodProfile"]
        DPAPI["Windows DPAPI Credential Vault<br/>(CurrentUser Scope)"]
        StateDB["SQLite (WAL Mode) State Store"]
        
        NamedPipe --> SafetyGate
        SafetyGate --> RollbackMgr
        RollbackMgr --> DPAPI
        RollbackMgr --> StateDB
    end

    subgraph USER_INTERFACES["用户交互界面"]
        WPF_App["LlamaCppWindowsManager.exe<br/>(WPF Desktop Console)"]
        CLI["llwmctl.exe<br/>(Native AOT High-Performance CLI)"]
    end

    CLIENTS -->|HTTP POST /v1/chat/completions| Gateway
    WPF_App -->|Named Pipe IPC| NamedPipe
    CLI -->|Named Pipe IPC| NamedPipe
    RollbackMgr -->|受控管理请求 / 独占凭据| Gateway
```

## 核心设计要点
1. **控制面与数据面彻底解耦**: UI 与 CLI 绝不直连数据面管理端口，绝不持有数据面管理密钥。所有控制指令（切换模型、回滚、暂停、恢复、重启）必须经由 Windows Named Pipe 路由至 Guard。
2. **零侵入安全隔离**: 系统自动检测主机上运行的其他 AI 模型进程（如独立部署的 Cyber-Tiel 或外部 Codex++ 驱动的模型），并将其严格分类为 `EXTERNAL_UNMANAGED` 或 `EXTERNAL_CODEX_OWNED`，禁止执行任何可能引发显存冲突的操作。
3. **高韧性状态自愈**: 当遭遇连续切换失败或显卡故障时，系统自动锁定（`FAULT_LOCKED`）并退回已知良好的稳定模型配置，保障客户端业务连续性。
