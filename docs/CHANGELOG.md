# Changelog

All notable changes to this project will be documented in this file.

## [3.0.0] - 2026-11-23

### Added
- **Local Model Conductor (LMC)** official release identity.
- **Unified API Gateway**: Fixed API facade at `http://127.0.0.1:19000/v1` and constant model ID `local-coder`.
- **LocalLLMManager.Guard**: Independent safety control plane running as background process.
- **ResourceSafetyGate**: Pre-flight evaluation for GPU VRAM, physical RAM, and memory commit limits.
- **Automatic Failure Rollback**: Millisecond-level fallback to `LastKnownGoodProfile` upon load timeout or health failure.
- **FIFO Request Queue & SSE Draining**: Seamless token streaming preservation during model switches.
- **External Process Shield**: Automatic detection and protection of external model processes (`EXTERNAL_UNMANAGED` and `EXTERNAL_CODEX_OWNED`).
- **Antigravity-Style Distribution**: PowerShell one-click installer (`install.ps1`) supporting DryRun, SemVer checks, and SHA-256 verification.
- **NSIS Windows Installer**: User-level installer (`LocalModelConductor-Setup-win-x64.exe`) with dual-language support and default data retention.
- **Portable ZIP Edition**: `LocalModelConductor-Portable-win-x64.zip` for zero-install portable deployment.
- **SPDX 2.3 SBOM & Checksum Manifests**: Enterprise-grade supply chain security compliance.
