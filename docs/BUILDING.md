# Local Model Conductor — Building from Source

## 1. 构建前置要求
- Windows 10 (19041+) 或 Windows 11 (x64)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [NSIS 3.13+](https://nsis.sourceforge.io/) (用于打包 Windows 安装程序)
- Git 2.40+

## 2. 源码构建流程

### 2.1 还原与编译
```powershell
# 还原依赖
dotnet restore LocalModelConductor.sln

# 编译整个解决方案 (Release 配置)
dotnet build LocalModelConductor.sln -c Release
```

### 2.2 运行自动化测试
```powershell
dotnet test tests/LocalLlmConsole.Tests/LocalLlmConsole.Tests.csproj -c Release
dotnet test phase4a_guard/src/LocalLLMManager.Guard.Tests/LocalLLMManager.Guard.Tests.csproj -c Release
```

### 2.3 生成自包含发布文件
```powershell
# 1. 发布 Native AOT CLI
dotnet publish src/LocalLlmConsole.ControlCli/LocalLlmConsole.ControlCli.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o dist/LMC-win-x64

# 2. 发布 WPF 桌面端
dotnet publish src/LocalLlmConsole.App/LocalLlmConsole.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/LMC-win-x64

# 3. 发布 Guard 后台守护服务
dotnet publish phase4a_guard/src/LocalLLMManager.Guard.Service/LocalLLMManager.Guard.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/LMC-win-x64/Guard
```

### 2.4 编译 NSIS 安装包
```powershell
makensis /INPUTCHARSET UTF8 installer/LocalModelConductor.nsi
```
输出位于 `dist/LocalModelConductor-Setup-win-x64.exe`。
