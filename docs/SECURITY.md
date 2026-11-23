# Security Policy

## 1. 安全边界与威胁模型
Local Model Conductor 设计运行在单机开发环境，遵循最小特权原则：
1. **Windows ACL Named Pipe**: 命名管道服务端仅允许运行 Guard 守护进程的当前 Windows 用户 SID 读写，阻止跨账户越权。
2. **Windows DPAPI 凭据保护**: 数据面管理密码加密保存在 `data\state\guard-credentials.dpapi.bin`，加密范围为 `DataProtectionScope.CurrentUser`。跨机器、跨用户无法解密。
3. **零特权提升**: 本软件不需要 Administrator 管理员权限，不注册 SYSTEM 权限的 Windows Service。

## 2. 漏洞报告
若发现安全漏洞或特权逃逸问题，请通过 GitHub Security Advisory 或私信核心维护者提交漏洞报告，请勿公开提 issue。
