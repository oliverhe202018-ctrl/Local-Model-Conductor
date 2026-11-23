# Local Model Conductor — Quickstart Guide

## 1. 启动与验证
1. 安装或解压后，运行 `LlamaCppWindowsManager.exe` 打开桌面图形管理控制台。
2. 守护进程 `Guard\LocalLLMManager.Guard.Service.exe` 会自动随桌面程序启动，或作为用户后台进程运行。
3. 打开命令行终端，运行：
   ```powershell
   llwmctl status
   llwmctl gateway status
   ```
   若输出 `"accepted": true`，说明管理通道已建立。

## 2. 添加模型与引擎
- **导入模型**: 将你的 `.gguf` 权重文件拷贝至 `data\models\`，或在 UI 界面点击 **Import Model**。
- **导入 Runtime**: 支持导入官方 llama.cpp 发行包（CPU、CUDA 12、Vulkan、AVX2）。

## 3. 配置客户端连接
在你的开发工具中，设置 OpenAI 兼容终结点：
- **API Base URL**: `http://127.0.0.1:19000/v1`
- **Model Name**: `local-coder`
- **API Key**: 留空或输入任意字符串（如 `local-dev`）

## 4. 故障排查
- 如果 CLI 返回 `The operation has timed out`，请检查 `LocalLLMManager.Guard.Service.exe` 是否在运行。
- 如果切换模型被拒绝并提示显存冲突，检查是否已有其他程序（如游戏或独立 llama-server）占满了显卡显存。
