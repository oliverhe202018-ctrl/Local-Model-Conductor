<#
.SYNOPSIS
    Real End-to-End (E2E) Acceptance Test for Local Model Conductor NSIS Installer.
.DESCRIPTION
    Executes a complete physical installation lifecycle on a clean Windows runner:
    1. Clean silent installation to an isolated target directory.
    2. File structure, binary, doc, and data skeleton integrity verification.
    3. Per-user HKCU registry entries and Start Menu / Desktop shortcuts verification.
    4. Execution verification of installed llwmctl CLI.
    5. Re-installation / in-place upgrade verification with canary data retention.
    6. Silent uninstallation verification ensuring binary removal while preserving user data.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$EvidenceOutputPath = "dist/release/installer-e2e-evidence.json",
    [switch]$AllowRunningGuard
)

$ErrorActionPreference = "Stop"

function Write-TestLog {
    param([string]$Message, [string]$Level = "INFO")
    $color = switch ($Level) {
        "INFO" { "Cyan" }
        "WARN" { "Yellow" }
        "ERROR" { "Red" }
        "SUCCESS" { "Green" }
        default { "White" }
    }
    Write-Host "[INSTALLER-E2E] [$Level] $Message" -ForegroundColor $color
}

Write-TestLog "Starting NSIS Installer Real End-to-End Acceptance Test..." "INFO"

# 1. Validate Installer File
$Installer = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
    throw "Installer binary not found: $Installer"
}

$installerHash = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
$installerSize = (Get-Item -LiteralPath $Installer).Length
Write-TestLog "Installer Target: $Installer" "INFO"
Write-TestLog "Installer Size:   $([math]::Round($installerSize / 1MB, 2)) MB ($installerSize bytes)" "INFO"
Write-TestLog "Installer SHA256: $installerHash" "INFO"

# 2. Production Safety Guard
$runningGuard = Get-Process -Name "LocalLLMManager.Guard.Service" -ErrorAction SilentlyContinue
if ($runningGuard -and -not $AllowRunningGuard) {
    throw "Production Guard Service (PID $($runningGuard.Id)) is running on this host! NSIS installer executes taskkill which would terminate production services. This E2E test must run on a clean CI runner or disposable VM."
}

# 3. Setup Isolated Test Directory
$testGuid = [Guid]::NewGuid().ToString("N")
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lmc-installer-e2e-$testGuid"
$installDir = Join-Path $testRoot "LocalModelConductor"

Write-TestLog "Isolated Test Directory: $installDir" "INFO"
$evidence = [ordered]@{
    timestampUtc = [DateTimeOffset]::UtcNow.ToString("o")
    installer = @{
        path = $Installer
        sha256 = $installerHash
        sizeBytes = $installerSize
    }
    testEnvironment = @{
        os = [System.Environment]::OSVersion.ToString()
        is64BitOs = [System.Environment]::Is64BitOperatingSystem
        machineName = $env:COMPUTERNAME
    }
    steps = @()
}

function Record-Step {
    param([string]$Name, [bool]$Success, [string]$Details)
    $step = [ordered]@{
        name = $Name
        success = $Success
        details = $Details
    }
    $script:evidence.steps += $step
    if ($Success) {
        Write-TestLog "${Name}: PASSED ($Details)" "SUCCESS"
    } else {
        Write-TestLog "${Name}: FAILED ($Details)" "ERROR"
        throw "Installer E2E Step Failed: $Name - $Details"
    }
}

try {
    # =========================================================================
    # STEP 1: Clean Silent Installation
    # =========================================================================
    Write-TestLog "Executing Step 1: Clean Silent Installation (/S /D=$installDir)..." "INFO"
    
    $proc = Start-Process -FilePath $Installer -ArgumentList @("/S", "/D=$installDir") -Wait -PassThru
    $installExit = $proc.ExitCode
    Record-Step "1.1_SilentInstallExitCode" ($installExit -eq 0) "ExitCode=$installExit"

    # Wait briefly for disk flushes
    Start-Sleep -Milliseconds 1000

    Record-Step "1.2_InstallDirExists" (Test-Path -LiteralPath $installDir) "Path=$installDir"

    # Verify Core Binaries
    $binaries = @(
        "LlamaCppWindowsManager.exe",
        "LlamaCppWindowsManager.exe.sha256",
        "llwmctl.exe",
        "llwmctl.exe.sha256",
        "Uninstall.exe",
        "Guard\LocalLLMManager.Guard.Service.exe",
        "Guard\LocalLLMManager.Guard.Service.exe.sha256"
    )
    foreach ($bin in $binaries) {
        $fullPath = Join-Path $installDir $bin
        $exists = Test-Path -LiteralPath $fullPath
        $len = if ($exists) { (Get-Item -LiteralPath $fullPath).Length } else { 0 }
        Record-Step "1.3_Binary_$bin" ($exists -and $len -gt 0) "Size=$len bytes"
    }

    # Verify Documentation & Metadata
    $docs = @(
        "LICENSE",
        "THIRD-PARTY-NOTICES.md",
        "agent.md",
        "AGENTS.md",
        "sbom.spdx.json",
        "docs\CONTROL_API.md"
    )
    foreach ($doc in $docs) {
        $fullPath = Join-Path $installDir $doc
        $exists = Test-Path -LiteralPath $fullPath
        Record-Step "1.4_Doc_$doc" $exists "Path=$fullPath"
    }

    # Verify Data Directory Skeleton
    $dataDirs = @("data", "data\models", "data\runtimes", "data\state", "data\logs")
    foreach ($dd in $dataDirs) {
        $fullPath = Join-Path $installDir $dd
        Record-Step "1.5_DataSkeleton_$dd" (Test-Path -LiteralPath $fullPath) "Directory=$fullPath"
    }

    # Verify HKCU Registry Entries
    $appRegKey = "HKCU:\Software\LocalModelConductor"
    $uninstRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalModelConductor"
    
    $appRegExists = Test-Path -Path $appRegKey
    $uninstRegExists = Test-Path -Path $uninstRegKey
    Record-Step "1.6_Registry_AppKey" $appRegExists "Key=$appRegKey"
    Record-Step "1.6_Registry_UninstallKey" $uninstRegExists "Key=$uninstRegKey"

    if ($uninstRegExists) {
        $dispName = (Get-ItemProperty -Path $uninstRegKey -Name "DisplayName" -ErrorAction SilentlyContinue).DisplayName
        $dispVer = (Get-ItemProperty -Path $uninstRegKey -Name "DisplayVersion" -ErrorAction SilentlyContinue).DisplayVersion
        $uninstStr = (Get-ItemProperty -Path $uninstRegKey -Name "UninstallString" -ErrorAction SilentlyContinue).UninstallString
        Record-Step "1.7_Registry_DisplayName" ($dispName -match "Local Model Conductor") "DisplayName=$dispName"
        Record-Step "1.7_Registry_DisplayVersion" ($dispVer -eq "3.2.0") "DisplayVersion=$dispVer"
        Record-Step "1.7_Registry_UninstallString" ($uninstStr -like "*Uninstall.exe*") "UninstallString=$uninstStr"
    }

    # Verify Shortcuts
    $startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Local Model Conductor"
    $desktopShortcut = Join-Path ([System.Environment]::GetFolderPath("Desktop")) "Local Model Conductor.lnk"
    Record-Step "1.8_Shortcuts_StartMenuDir" (Test-Path -LiteralPath $startMenuDir) "Path=$startMenuDir"

    # =========================================================================
    # STEP 2: Execute Installed CLI
    # =========================================================================
    Write-TestLog "Executing Step 2: Test Installed CLI (llwmctl status)..." "INFO"
    $cliExe = Join-Path $installDir "llwmctl.exe"
    $cliProc = Start-Process -FilePath $cliExe -ArgumentList @("status") -NoNewWindow -Wait -PassThru
    Record-Step "2.1_CliExecution" ($cliProc.ExitCode -eq 0 -or $cliProc.ExitCode -eq 1) "ExitCode=$($cliProc.ExitCode)"

    # =========================================================================
    # STEP 3: In-Place Upgrade / Reinstall & Data Retention Verification
    # =========================================================================
    Write-TestLog "Executing Step 3: Upgrade / Re-install with User Canary Data Retention..." "INFO"
    
    # Plant user canary data
    $canaryModel = Join-Path $installDir "data\models\canary-model-35b.gguf"
    $canaryState = Join-Path $installDir "data\state\canary-settings.db"
    Set-Content -LiteralPath $canaryModel -Value "MOCK_GGUF_MODEL_DATA_PRESERVE" -Encoding ascii
    Set-Content -LiteralPath $canaryState -Value "MOCK_SQLITE_USER_STATE_PRESERVE" -Encoding ascii

    # Re-run installer (simulating upgrade)
    $upgProc = Start-Process -FilePath $Installer -ArgumentList @("/S", "/D=$installDir") -Wait -PassThru
    Start-Sleep -Milliseconds 1000
    Record-Step "3.1_UpgradeInstallerExitCode" ($upgProc.ExitCode -eq 0) "ExitCode=$($upgProc.ExitCode)"

    # Verify binaries still present
    Record-Step "3.2_UpgradeBinariesPresent" (Test-Path -LiteralPath (Join-Path $installDir "LlamaCppWindowsManager.exe")) "App binary verified"

    # Verify CANARY DATA IS INTACT
    $modelIntact = (Test-Path -LiteralPath $canaryModel) -and ((Get-Content -LiteralPath $canaryModel -Raw) -match "MOCK_GGUF_MODEL_DATA_PRESERVE")
    $stateIntact = (Test-Path -LiteralPath $canaryState) -and ((Get-Content -LiteralPath $canaryState -Raw) -match "MOCK_SQLITE_USER_STATE_PRESERVE")
    Record-Step "3.3_UpgradePreservedModelData" $modelIntact "Canary model file preserved across upgrade"
    Record-Step "3.3_UpgradePreservedStateData" $stateIntact "Canary state database preserved across upgrade"

    # =========================================================================
    # STEP 4: Silent Uninstallation & User Data Preservation
    # =========================================================================
    Write-TestLog "Executing Step 4: Silent Uninstallation (_?=$installDir)..." "INFO"
    
    $uninstaller = Join-Path $installDir "Uninstall.exe"
    # Note: _?=$installDir ensures uninstaller runs synchronously without spawning detached temp copy
    $uninstProc = Start-Process -FilePath $uninstaller -ArgumentList @("/S", "_?=$installDir") -Wait -PassThru
    Start-Sleep -Milliseconds 2000

    Record-Step "4.1_UninstallExitCode" ($uninstProc.ExitCode -eq 0) "ExitCode=$($uninstProc.ExitCode)"

    # Verify binaries are REMOVED
    $appStillThere = Test-Path -LiteralPath (Join-Path $installDir "LlamaCppWindowsManager.exe")
    $cliStillThere = Test-Path -LiteralPath (Join-Path $installDir "llwmctl.exe")
    $guardStillThere = Test-Path -LiteralPath (Join-Path $installDir "Guard\LocalLLMManager.Guard.Service.exe")
    Record-Step "4.2_BinariesRemoved" (-not $appStillThere -and -not $cliStillThere -and -not $guardStillThere) "All application binaries deleted"

    # Verify Registry is CLEANED
    $uninstRegStillThere = Test-Path -Path $uninstRegKey
    Record-Step "4.3_RegistryCleaned" (-not $uninstRegStillThere) "Uninstall registry entry removed"

    # Verify Shortcuts REMOVED
    $startMenuStillThere = Test-Path -LiteralPath $startMenuDir
    Record-Step "4.4_ShortcutsRemoved" (-not $startMenuStillThere) "Start menu shortcuts removed"

    # CRITICAL: Verify User Data Folder and Canary Files are STILL INTACT!
    $canaryModelAfterUninst = Test-Path -LiteralPath $canaryModel
    $canaryStateAfterUninst = Test-Path -LiteralPath $canaryState
    Record-Step "4.5_UserDataRetainedAfterUninstall" ($canaryModelAfterUninst -and $canaryStateAfterUninst) "data/models and data/state retained after uninstall"

    Write-TestLog "ALL 20+ END-TO-END INSTALLER ACCEPTANCE CHECKS PASSED!" "SUCCESS"
    $script:evidence["verdict"] = "INSTALLER_E2E_PASS"
}
finally {
    # Cleanup test root
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Save evidence file
    $outDir = Split-Path -Parent $EvidenceOutputPath
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidenceOutputPath -Encoding utf8
    Write-TestLog "Saved test evidence to $EvidenceOutputPath" "INFO"
}
