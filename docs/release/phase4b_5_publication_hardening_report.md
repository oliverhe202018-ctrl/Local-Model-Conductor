# Phase 4B.5 — Public Repository CI & Release Pipeline Hardening Final Report

**Project**: Local Model Conductor (LMC, 本地模型调度台)  
**Official Repository**: `https://github.com/oliverhe202018-ctrl/Local-Model-Conductor`  
**Public Git Baseline**: `17c09a2e0d6c464ac601c7d72326fcd6607af960` (on `main`)  
**Isolated Worktree**: `D:\project\本地模型计算\phase4b_5_worktree`  
**Hardening Branch**: `phase4b-5-publication-hardening`  
**Final Audit Verdict**: **`PHASE4B_5_PASS`**  
**Execution Timestamp**: 2026-11-23T19:42:00+08:00  

---

## 1. Executive Summary

Phase 4B.5 successfully completed all CI and release pipeline hardening tasks for the public repository under strict execution isolation:

1. **Automated CI Gates Restored and Strengthened**: Removed `continue-on-error: true` from `.github/workflows/build.yml`. Replaced incompatible VSTest logger syntax with native Microsoft.Testing.Platform (MTP) `--report-xunit-trx`. All code-shape gates and automated test suites are strictly enforced before any PR or push merges.
2. **Distribution Packaging Pipeline Built**: Created `scripts/build-distribution.ps1`, integrating Native AOT CLI (`llwmctl`), Single-File WPF Desktop App, and Single-File Guard Service. Added pre-packaging validation gate (13 required items verified), automated NSIS compilation (`makensis.exe`), portable zip generation, and automated generation of `SHA256SUMS.txt` and `release-manifest.json` (`artifacts` array).
3. **NSIS Installer Script Fixed**: Corrected `installer/LocalModelConductor.nsi` input/output path definitions and removed obsolete IIS `web.config` references. Successfully compiled `LocalModelConductor-Setup-win-x64.exe` (83.87 MB).
4. **Bootstrapper Script (`install.ps1`) Hardened**: Refactored `install.ps1` to seamlessly parse standard `release-manifest.json` (`artifacts` array) and fallback to plain-text `SHA256SUMS.txt`. Implemented fail-closed checksum enforcement and user-friendly error guidance when querying an empty repository prior to the first official release. Expanded `installer/install.ps1.tests.ps1` to cover 10 rigorous test scenarios (10/10 passed).
5. **Upstream Code & Shape Limits Harmonized**: Refactored oversized classes (`GuardControlCoordinator.cs` and `Phase4AGuardAcceptanceTests.cs`) into partial classes adhering to the $\le 500$ and $\le 800$ line thresholds. Migrated Guard tests to `tests/` directory and updated project dependencies to `Microsoft.Testing.Platform`.
6. **Zero Disruption to Production**: Zero downtime or disturbance to production processes (Caddy on `19000`, llama-server on `8082`, controller on `3001`, Codex++ on `57321`). No Git Tags or GitHub Releases were created, and no push to `main` was made.

---

## 2. Itemized Problem & Solution Matrix

| Component | Root Cause / Issue Identified | Hardening Fix Implemented | Verification Result |
| :--- | :--- | :--- | :--- |
| **Code Shape Limits** (`scripts/check-code-shape.ps1`) | `GuardControlCoordinator.cs` was 746 lines (limit: 500); `Phase4AGuardAcceptanceTests.cs` was 932 lines (limit: 500 under `src/`). | Split `GuardControlCoordinator` into partial class with `.Switching.cs`. Moved Guard tests to `tests/` (limit 800) and split into `.SafetyAndLifecycle.cs`. | `check-code-shape.ps1` PASSED with 0 errors. |
| **Test Runner Compatibility** (`global.json`) | `global.json` enforces `Microsoft.Testing.Platform`, causing VSTest `--logger` arguments and VSTest test runners to fail with Exit Code 5. | Migrated `LocalLLMManager.Guard.Tests.csproj` to `xunit.v3.mtp-v2 4.0.3` with `<NoWarn>xUnit1051</NoWarn>`. Updated `.github/workflows/build.yml` to use `--report-xunit-trx`. | Guard tests pass (4/4). Full test suite produces TRX reports via MTP. |
| **Mojibake in Acceptance Tests** | Hardcoded path strings contained corrupted Chinese characters (`鏈湴妯″瀷璁＄畻`) causing Win32 DirectoryNotFound errors. | Corrected paths to UTF-8 `本地模型计算`. | 4/4 Guard acceptance tests passed cleanly in 1m 30s. |
| **Host CMake Resolution in Tests** | Upstream test `RuntimeBuildExecutionServiceRunsNativeBuildRegistersRuntimeAndDeletesSource` failed when `cmake.exe` was not in PATH. | Added `LLAMA_CPP_CMAKE_PATH` environment variable support in `HostExecutableResolver.CMakeExe()` and added dummy cmake cleanup fixture. | 6/6 RuntimeBuildExecution tests passed. |
| **Upstream Secret & Branding Tests** | Static analysis tests asserted upstream Inno Setup workflow secrets and branding `# llama.cpp Windows Manager`. | Restored upstream signed workflow as `.github/workflows/trusted-release.yml`. Updated branding tests to accept `# Local Model Conductor`. | 11/11 ReleaseRepository tests passed. 4/4 ReleaseWorkflowSecrets tests passed. |
| **Third-Party Notices** | Test asserted `Microsoft.Data.Sqlite 10.0.15` package notice format. | Corrected package version string in `THIRD-PARTY-NOTICES.md`. | Verified by test suite. |
| **NSIS Installer Input Paths** | `LocalModelConductor.nsi` referenced hardcoded dev machine paths (`..\..\phase4b_3_release\...`) and non-existent `Guard/web.config`. | Parameterized `SOURCE_DIR`, `OUTPUT_DIR`, `APP_ICON`, and removed `web.config`. Default paths point to `dist/LlamaCppWindowsManager-win-x64` and `dist/release`. | NSIS compiler `makensis.exe` compiled installer without warnings/errors. |
| **Release Build Script** | Missing unified build script that ties together publishing, staging, validation, NSIS, ZIP, and manifest generation. | Authored `scripts/build-distribution.ps1` with 5-stage pipeline including strict 13-item Pre-Packaging Integrity Gate. Passed `-p:NuGetLockFilePath=packages.publish.lock.json` for Native AOT. | Generated Setup EXE (83.87 MB), Portable ZIP (115.68 MB), `SHA256SUMS.txt`, and `release-manifest.json`. |
| **Bootstrapper Manifest Parsing** | `install.ps1` only parsed legacy key-value manifest format and failed on standard `artifacts` array or empty releases. | Rewrote `install.ps1` to natively parse `artifacts` array, fall back to `SHA256SUMS.txt`, enforce fail-closed SHA-256 checks, and display helpful guidance prior to initial release. | All 10 test scenarios in `installer/install.ps1.tests.ps1` passed. |
| **README Documentation** | Installation sections referenced non-existent live releases without explaining pre-release status. | Updated `README.md` and `README.zh-CN.md` to note "Available after the first official release" with local script testing instructions and documentation links. | Validated documentation integrity. |

---

## 3. Full Test Suite Execution Summary

All tests executed in `D:\project\本地模型计算\phase4b_5_worktree` under Release configuration:

```
================================================================================
Test Suite / Validation Gate               Total    Passed    Failed    Duration
================================================================================
1. scripts/check-code-shape.ps1               --      PASS         0       2.9 s
2. LocalLLMManager.Guard.Tests                 4         4         0     1m 30 s
3. LocalLlmConsole.UiTests                   140       140         0      36.8 s
4. LocalLlmConsole.Tests                    1392      1392         0     1m 26 s
5. installer/install.ps1.tests.ps1            10        10         0       4.8 s
================================================================================
TOTAL AUTOMATED TEST CASES                  1546      1546         0     ~3m 40s
================================================================================
```

### Detailed Breakdown of Bootstrapper Tests (`installer/install.ps1.tests.ps1`):
1. `DryRun - Stable Channel`: **PASS** (1,438 ms)
2. `DryRun - Specified Version (v3.0.0)`: **PASS** (1,315 ms)
3. `SemVer Validation - Reject Invalid Version`: **PASS** (260 ms)
4. `Local Installer - SHA-256 Match and DryRun`: **PASS** (346 ms)
5. `Checksum Tampering Detection - Immediate Abort`: **PASS** (341 ms)
6. `Missing Checksum - Fail-Closed Rejection`: **PASS** (295 ms)
7. `Release Manifest - Standard Artifacts Array Parsing`: **PASS** (361 ms)
8. `Release Manifest - Missing Installer Record Abort`: **PASS** (353 ms)
9. `SHA256SUMS - Plain Text Format Parsing`: **PASS** (274 ms)
10. `Local Installer - Non-Existent Path Abort`: **PASS** (276 ms)

---

## 4. Release Distribution Artifacts

The distribution pipeline was executed with:
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-distribution.ps1 -Configuration Release -Version v3.0.0
```

### Artifact Summary (`dist/release`):

| Artifact | Size | SHA-256 Digest | Status |
| :--- | :--- | :--- | :--- |
| **`LocalModelConductor-Setup-win-x64.exe`** | 87,946,677 bytes (83.87 MB) | `c9b99ae5b0bcd0ddff1996fb4e6999d5b1f9f477ffd1cf1f06fadaee7f32b2d3` | Verified |
| **`LocalModelConductor-Portable-win-x64.zip`** | 121,298,978 bytes (115.68 MB) | `ed1cadc841d4f859e666ac33c6912a6d3ea56e49bfb9789d4d06435edc775f35` | Verified |
| **`SHA256SUMS.txt`** | 164 bytes | Matches artifact checksums | Verified |
| **`release-manifest.json`** | 358 bytes | Valid JSON, `artifacts` array matching exact checksums | Verified |

### Pre-Packaging Integrity Gate Verification:
All 13 required items verified in `dist/LlamaCppWindowsManager-win-x64`:
- `LlamaCppWindowsManager.exe` & companion `.sha256`
- `llwmctl.exe` & companion `.sha256`
- `Guard/LocalLLMManager.Guard.Service.exe` & companion `.sha256`
- `LICENSE`
- `THIRD-PARTY-NOTICES.md`
- `agent.md` & `AGENTS.md`
- `sbom.spdx.json`
- `docs/CONTROL_API.md`
- `licenses/Apache-2.0.txt`

---

## 5. Production Environmental Isolation Confirmation

All production services were monitored throughout Phase 4B.5 and confirmed completely intact:

| Port | Service | Expected PID | Audited State | Status |
| :--- | :--- | :--- | :--- | :--- |
| **`19000`** | Caddy Unified Gateway | `41032` | `127.0.0.1:19000 Listen` | Unaffected / Healthy |
| **`8082`** | llama-server Engine | `37948` | `127.0.0.1:8082 Listen` | Unaffected / Healthy |
| **`3001`** | FastChat Controller | `25512` | `127.0.0.1:3001 Listen` | Unaffected / Healthy |
| **`57321`** | Codex++ Production Process | `4308` | `127.0.0.1:57321 Listen` | Unaffected / Healthy |

---

## 6. Git Worktree Status & Release Boundaries

- **Worktree Path**: `D:\project\本地模型计算\phase4b_5_worktree`
- **Branch**: `phase4b-5-publication-hardening`
- **Main Repo Working Copy**: `D:\project\本地模型计算\phase4b_windows_manager_fork\repo` remains completely untouched.
- **Strict Boundaries Enforced**:
  - No `git push --force` or `--force-with-lease`.
  - No push to remote `main`.
  - No Git Tags created.
  - No GitHub Releases created.
  - No binary artifacts committed to repository history.

---

## 7. Next Phase Recommendation

With all CI test gates, distribution build scripts, NSIS packaging configurations, bootstrapper compatibility, and documentation fully verified and hardened under `PHASE4B_5_PASS`:

The repository is now in an optimal state for the team to either:
1. Merge `phase4b-5-publication-hardening` into `main` via authorized commit/PR when ready.
2. Proceed with Phase 4C production model cutover and live switchboard integration.
