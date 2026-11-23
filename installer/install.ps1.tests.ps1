# Local Model Conductor - Comprehensive Bootstrapper Test Suite (install.ps1.tests.ps1)
param(
    [string]$ScriptPath,
    [string]$SetupExePath
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ScriptPath) {
    $ScriptPath = Join-Path $RepoRoot "install.ps1"
}
if (-not $SetupExePath) {
    $candidateExe = Join-Path $RepoRoot "dist/release/LocalModelConductor-Setup-win-x64.exe"
    if (Test-Path $candidateExe) {
        $SetupExePath = $candidateExe
    } else {
        $SetupExePath = "D:\project\本地模型计算\phase4b_4_release_closure\dist\LocalModelConductor-Setup-win-x64.exe"
    }
}

Write-Host "Target Script: $ScriptPath" -ForegroundColor Cyan
Write-Host "Target Setup Exe Fixture: $SetupExePath" -ForegroundColor Cyan

$testResults = @()

function Invoke-ScriptProcess {
    param([string[]]$Arguments)
    $allArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ScriptPath) + $Arguments
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = [string]::Join(" ", ($allArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }))
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit(15000)

    return [PSCustomObject]@{
        ExitCode = $p.ExitCode
        Output = ($stdout + "`n" + $stderr).Trim()
    }
}

function Run-TestCase {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "`n>>> [RUNNING TEST] $Name" -ForegroundColor Cyan
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $Action
        $sw.Stop()
        Write-Host ">>> [PASS] ${Name} ($($sw.ElapsedMilliseconds) ms)" -ForegroundColor Green
        return [PSCustomObject]@{
            TestName = $Name
            Status = "PASS"
            DurationMs = $sw.ElapsedMilliseconds
            Error = $null
        }
    }
    catch {
        Write-Host ">>> [FAIL] ${Name}: $($_.Exception.Message)" -ForegroundColor Red
        return [PSCustomObject]@{
            TestName = $Name
            Status = "FAIL"
            DurationMs = 0
            Error = $_.Exception.Message
        }
    }
}

# --- Test 1: DryRun Mode ---
$testResults += Run-TestCase "DryRun - Stable Channel" {
    $res = Invoke-ScriptProcess @("-DryRun", "-Channel", "stable")
    if ($res.ExitCode -ne 0 -or $res.Output -notmatch "\[DRY-RUN\] Dry run completed") {
        throw "DryRun failed. ExitCode=$($res.ExitCode). Output: $($res.Output)"
    }
}

# --- Test 2: DryRun with Specific Version ---
$testResults += Run-TestCase "DryRun - Specified Version (v3.0.0)" {
    $res = Invoke-ScriptProcess @("-DryRun", "-Version", "3.0.0")
    if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Target version specified: 3.0.0" -or $res.Output -notmatch "\[DRY-RUN\]") {
        throw "Specified version DryRun failed. ExitCode=$($res.ExitCode). Output: $($res.Output)"
    }
}

# --- Test 3: SemVer Validation Rejection ---
$testResults += Run-TestCase "SemVer Validation - Reject Invalid Version" {
    $res = Invoke-ScriptProcess @("-DryRun", "-Version", "invalid..version")
    if ($res.ExitCode -eq 0 -or $res.Output -notmatch "Invalid SemVer format") {
        throw "Failed to reject invalid SemVer. ExitCode=$($res.ExitCode). Output: $($res.Output)"
    }
}

# --- Test 4: Local Installer with Companion SHA-256 ---
$testResults += Run-TestCase "Local Installer - SHA-256 Match and DryRun" {
    $dummyExe = Join-Path ([System.IO.Path]::GetTempPath()) "MockSetup.exe"
    Set-Content -Path $dummyExe -Value "MOCK_SETUP_PAYLOAD_VALID"
    $dummyHash = (Get-FileHash -Path $dummyExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$dummyExe.sha256" -Value "$dummyHash  MockSetup.exe"

    try {
        $res = Invoke-ScriptProcess @("-LocalInstallerPath", $dummyExe, "-DryRun")
        if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Loaded companion SHA-256" -or $res.Output -notmatch "\[DRY-RUN\]") {
            throw "Local installer verification failed. ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $dummyExe, "$dummyExe.sha256" -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 5: Checksum Tampering Detection ---
$testResults += Run-TestCase "Checksum Tampering Detection - Immediate Abort" {
    $tamperedExe = Join-Path ([System.IO.Path]::GetTempPath()) "TamperedSetup.exe"
    Set-Content -Path $tamperedExe -Value "CORRUPTED_INSTALLER_PAYLOAD"
    $tamperedSha = "$tamperedExe.sha256"
    Set-Content -Path $tamperedSha -Value "0000000000000000000000000000000000000000000000000000000000000000  TamperedSetup.exe"

    try {
        $res = Invoke-ScriptProcess @("-LocalInstallerPath", $tamperedExe)
        if ($res.ExitCode -eq 0 -or $res.Output -notmatch "SHA-256 checksum mismatch") {
            throw "Expected checksum mismatch error was not caught! ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $tamperedExe, $tamperedSha -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 6: Missing Checksum Fail-Closed ---
$testResults += Run-TestCase "Missing Checksum - Fail-Closed Rejection" {
    $noHashExe = Join-Path ([System.IO.Path]::GetTempPath()) "NoHashSetup.exe"
    Set-Content -Path $noHashExe -Value "NO_HASH_SETUP_PAYLOAD"

    try {
        $res = Invoke-ScriptProcess @("-LocalInstallerPath", $noHashExe)
        if ($res.ExitCode -eq 0 -or $res.Output -notmatch "Security verification failed: No valid SHA-256 checksum") {
            throw "Expected fail-closed rejection was not caught! ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $noHashExe -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 7: Release Manifest JSON Parsing (artifacts array) ---
$testResults += Run-TestCase "Release Manifest - Standard Artifacts Array Parsing" {
    $tempManifest = Join-Path ([System.IO.Path]::GetTempPath()) "test-manifest-artifacts.json"
    $dummyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    $manifestData = [ordered]@{
        version = "v3.0.0"
        generatedAtUtc = "2026-11-23T12:00:00Z"
        artifacts = @(
            @{
                filename = "LocalModelConductor-Setup-win-x64.exe"
                sha256 = $dummyHash
                sizeBytes = 104857600
            },
            @{
                filename = "LocalModelConductor-Portable-win-x64.zip"
                sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                sizeBytes = 94371840
            }
        )
    } | ConvertTo-Json -Depth 5
    Set-Content -Path $tempManifest -Value $manifestData -Encoding utf8

    try {
        $res = Invoke-ScriptProcess @("-LocalManifestPath", $tempManifest, "-DryRun")
        if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Expected SHA-256: $dummyHash" -or $res.Output -notmatch "\[DRY-RUN\]") {
            throw "Failed to parse artifacts array manifest. ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $tempManifest -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 8: Release Manifest Missing Installer Record ---
$testResults += Run-TestCase "Release Manifest - Missing Installer Record Abort" {
    $tempManifest = Join-Path ([System.IO.Path]::GetTempPath()) "test-manifest-missing-installer.json"
    $manifestData = [ordered]@{
        version = "v3.0.0"
        artifacts = @(
            @{
                filename = "UnrelatedFile.zip"
                sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                sizeBytes = 100
            }
        )
    } | ConvertTo-Json -Depth 5
    Set-Content -Path $tempManifest -Value $manifestData -Encoding utf8

    try {
        $res = Invoke-ScriptProcess @("-LocalManifestPath", $tempManifest, "-DryRun")
        if ($res.ExitCode -eq 0 -or $res.Output -notmatch "does not contain an entry for 'LocalModelConductor-Setup-\*\.exe'") {
            throw "Expected missing installer record error was not caught! ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $tempManifest -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 9: Plain Text SHA256SUMS.txt Parsing ---
$testResults += Run-TestCase "SHA256SUMS - Plain Text Format Parsing" {
    $tempSums = Join-Path ([System.IO.Path]::GetTempPath()) "SHA256SUMS.txt"
    $dummyHash = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"
    $sumsData = "$dummyHash  LocalModelConductor-Setup-win-x64.exe`r`n1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef  Other.zip"
    Set-Content -Path $tempSums -Value $sumsData -Encoding ascii

    try {
        $res = Invoke-ScriptProcess @("-LocalManifestPath", $tempSums, "-DryRun")
        if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Expected SHA-256: $dummyHash" -or $res.Output -notmatch "\[DRY-RUN\]") {
            throw "Failed to parse SHA256SUMS text manifest. ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $tempSums -Force -ErrorAction SilentlyContinue
    }
}

# --- Test 10: Non-existent Local Installer Path Abort ---
$testResults += Run-TestCase "Local Installer - Non-Existent Path Abort" {
    $res = Invoke-ScriptProcess @("-LocalInstallerPath", "C:\NonExistentDirectory\FakeSetup.exe")
    if ($res.ExitCode -eq 0 -or $res.Output -notmatch "Specified -LocalInstallerPath does not exist") {
        throw "Failed to abort on non-existent LocalInstallerPath. ExitCode=$($res.ExitCode). Output: $($res.Output)"
    }
}

Write-Host "`n=== BOOTSTRAPPER TEST SUMMARY ===" -ForegroundColor Yellow
$testResults | Format-Table -AutoSize
$failed = $testResults | Where-Object { $_.Status -ne "PASS" }
if ($failed.Count -gt 0) {
    throw "Bootstrapper test suite had $($failed.Count) failures."
}

Write-Host "All $($testResults.Count) bootstrapper test cases passed successfully!" -ForegroundColor Green
