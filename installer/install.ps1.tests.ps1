# Local Model Conductor - Bootstrapper Test Suite (install.ps1.tests.ps1)
param(
    [string]$ScriptPath = "D:\project\本地模型计算\phase4b_4_release_closure\install.ps1",
    [string]$SetupExePath = "D:\project\本地模型计算\phase4b_4_release_closure\dist\LocalModelConductor-Setup-win-x64.exe"
)

$ErrorActionPreference = "Stop"
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

# --- Test 4: Local Installer with SHA-256 Verification ---
$testResults += Run-TestCase "Local Installer - SHA-256 Match and DryRun" {
    $res = Invoke-ScriptProcess @("-LocalInstallerPath", $SetupExePath, "-DryRun")
    if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Loaded companion SHA-256" -or $res.Output -notmatch "\[DRY-RUN\]") {
        throw "Local installer verification failed. ExitCode=$($res.ExitCode). Output: $($res.Output)"
    }
}

# --- Test 5: SHA-256 Tampering / Mismatch Detection ---
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

# --- Test 6: Local Release Manifest Parsing ---
$testResults += Run-TestCase "Manifest Parsing - Version & Checksum Matching" {
    $tempManifest = Join-Path ([System.IO.Path]::GetTempPath()) "test-manifest.json"
    $manifestData = @{
        version = "v3.0.0"
        installerAsset = "LocalModelConductor-Setup-win-x64.exe"
        sha256 = (Get-FileHash -Path $SetupExePath -Algorithm SHA256).Hash
        isPrerelease = $false
    } | ConvertTo-Json
    Set-Content -Path $tempManifest -Value $manifestData -Encoding utf8

    try {
        $res = Invoke-ScriptProcess @("-LocalManifestPath", $tempManifest, "-DryRun")
        if ($res.ExitCode -ne 0 -or $res.Output -notmatch "Loading local release manifest" -or $res.Output -notmatch "\[DRY-RUN\]") {
            throw "Failed to parse local release manifest. ExitCode=$($res.ExitCode). Output: $($res.Output)"
        }
    }
    finally {
        Remove-Item $tempManifest -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n=== BOOTSTRAPPER TEST SUMMARY ===" -ForegroundColor Yellow
$testResults | Format-Table -AutoSize
$failed = $testResults | Where-Object { $_.Status -ne "PASS" }
if ($failed.Count -gt 0) {
    throw "Bootstrapper test suite had $($failed.Count) failures."
}

$evidenceOut = "D:\project\本地模型计算\phase4b_4_release_closure\installer_bootstrapper_evidence.json"
$testResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidenceOut -Encoding utf8
Write-Host "Wrote evidence to $evidenceOut" -ForegroundColor Green
