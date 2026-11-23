<#
.SYNOPSIS
    Builds the complete distribution bundle for Local Model Conductor (LMC).
.DESCRIPTION
    Compiles all binaries (Native AOT CLI, Single-File WPF App, Single-File Guard Service),
    stages all documentation/licenses/SBOM, runs the pre-packaging integrity gate,
    builds the official NSIS installer, generates the Portable ZIP, and calculates
    SHA-256 digests and release-manifest.json.
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "v3.0.0",
    [string]$DistDir = "dist/LlamaCppWindowsManager-win-x64",
    [string]$ReleaseDir = "dist/release",
    [string]$MakensisPath,
    [switch]$SkipPublish,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-DistLog {
    param([string]$Message, [string]$Level = "INFO")
    $color = switch ($Level) {
        "INFO" { "Cyan" }
        "WARN" { "Yellow" }
        "ERROR" { "Red" }
        "SUCCESS" { "Green" }
        default { "White" }
    }
    Write-Host "[BUILD-DIST] [$Level] $Message" -ForegroundColor $color
}

# Resolve makensis executable
$makensis = $MakensisPath
if (-not $makensis) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $makensis = $command.Source }
}
if (-not $makensis) {
    $searchLocations = @(
        "C:\Program Files (x86)\NSIS\makensis.exe",
        "C:\Program Files\NSIS\makensis.exe",
        "C:\ProgramData\chocolatey\bin\makensis.exe",
        "D:\project\本地模型计算\tools\nsis-3.13\Bin\makensis.exe",
        "D:\project\本地模型计算\tools\nsis-3.13\makensis.exe"
    )
    foreach ($cand in $searchLocations) {
        if (Test-Path -LiteralPath $cand) {
            $makensis = $cand
            break
        }
    }
}

$fullDist = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $DistDir))
$fullRelease = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $ReleaseDir))
$iconPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "src/LocalLlmConsole.App/Assets/AppIcon.ico"))

Write-DistLog "Repository Root: $RepoRoot" "INFO"
Write-DistLog "Staging Directory: $fullDist" "INFO"
Write-DistLog "Release Directory: $fullRelease" "INFO"
Write-DistLog "NSIS Compiler: $(if ($makensis) { $makensis } else { '<not found>' })" "INFO"

# --- 1. Publish Binaries and Supporting Assets ---
if (-not $SkipPublish) {
    Write-DistLog "Publishing binaries for win-x64 ($Configuration)..." "INFO"
    New-Item -ItemType Directory -Path $fullDist, (Join-Path $fullDist "Guard"), (Join-Path $fullDist "docs"), (Join-Path $fullDist "licenses"), $fullRelease -Force | Out-Null

    # 1.1 Native AOT CLI
    Write-DistLog "1/3 Publishing Native AOT CLI (llwmctl)..." "INFO"
    dotnet publish (Join-Path $RepoRoot "src/LocalLlmConsole.ControlCli/LocalLlmConsole.ControlCli.csproj") `
        -c $Configuration -r win-x64 --self-contained true -p:PublishAot=true -p:NuGetLockFilePath=packages.publish.lock.json -o $fullDist
    if ($LASTEXITCODE -ne 0) { throw "Native AOT CLI publish failed." }

    # 1.2 WPF Desktop Application
    Write-DistLog "2/3 Publishing WPF Desktop Application..." "INFO"
    dotnet publish (Join-Path $RepoRoot "src/LocalLlmConsole.App/LocalLlmConsole.App.csproj") `
        -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $fullDist
    if ($LASTEXITCODE -ne 0) { throw "WPF App publish failed." }

    # 1.3 Guard Safety Service
    Write-DistLog "3/3 Publishing Guard Safety Service..." "INFO"
    $guardDist = Join-Path $fullDist "Guard"
    dotnet publish (Join-Path $RepoRoot "src/Guard/LocalLLMManager.Guard.Service/LocalLLMManager.Guard.Service.csproj") `
        -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $guardDist
    if ($LASTEXITCODE -ne 0) { throw "Guard Service publish failed." }

    # 1.4 Copy Documentation, Licenses, and Metadata
    Write-DistLog "Staging documentation, licenses, and SBOM..." "INFO"
    Copy-Item (Join-Path $RepoRoot "LICENSE") (Join-Path $fullDist "LICENSE") -Force
    Copy-Item (Join-Path $RepoRoot "THIRD-PARTY-NOTICES.md") (Join-Path $fullDist "THIRD-PARTY-NOTICES.md") -Force
    Copy-Item (Join-Path $RepoRoot "agent.md") (Join-Path $fullDist "agent.md") -Force
    Copy-Item (Join-Path $RepoRoot "AGENTS.md") (Join-Path $fullDist "AGENTS.md") -Force
    Copy-Item (Join-Path $RepoRoot "docs/CONTROL_API.md") (Join-Path $fullDist "docs/CONTROL_API.md") -Force
    Copy-Item (Join-Path $RepoRoot "licenses/*") (Join-Path $fullDist "licenses") -Recurse -Force

    # Generate SPDX SBOM
    $sbomTarget = Join-Path $fullDist "sbom.spdx.json"
    Write-DistLog "Generating SPDX SBOM to $sbomTarget ..." "INFO"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "scripts/new-sbom.ps1") -OutputPath $sbomTarget -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed." }

    # 1.5 Generate Companion SHA-256 Hashes
    Write-DistLog "Generating companion SHA-256 digests..." "INFO"
    $hashTargets = @(
        (Join-Path $fullDist "LlamaCppWindowsManager.exe"),
        (Join-Path $fullDist "llwmctl.exe"),
        (Join-Path $fullDist "Guard/LocalLLMManager.Guard.Service.exe")
    )
    foreach ($exe in $hashTargets) {
        if (-not (Test-Path -LiteralPath $exe)) { throw "Required executable not found for companion hashing: $exe" }
        $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        $leaf = Split-Path -Leaf $exe
        Set-Content -LiteralPath "$exe.sha256" -Value "$hash  $leaf" -Encoding ascii
    }

    # 1.6 Remove any stray PDBs and build artifacts
    Get-ChildItem -Path $fullDist -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -Path $fullDist -Recurse -Filter web.config -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -Path $fullDist -Recurse -Filter *.publish.lock.json -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -Path $fullDist -Recurse -Filter *.endpoints.json -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

# --- 2. Pre-Packaging Validation Gate ---
Write-DistLog "Executing Pre-Packaging Integrity Gate..." "INFO"
$requiredFiles = @(
    "LlamaCppWindowsManager.exe",
    "LlamaCppWindowsManager.exe.sha256",
    "llwmctl.exe",
    "llwmctl.exe.sha256",
    "Guard/LocalLLMManager.Guard.Service.exe",
    "Guard/LocalLLMManager.Guard.Service.exe.sha256",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "agent.md",
    "AGENTS.md",
    "sbom.spdx.json",
    "docs/CONTROL_API.md",
    "licenses/Apache-2.0.txt"
)

foreach ($rel in $requiredFiles) {
    $fullPath = Join-Path $fullDist $rel
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "Pre-packaging gate failed: Required artifact missing: $rel ($fullPath)"
    }
    $len = (Get-Item -LiteralPath $fullPath).Length
    if ($len -le 0) {
        throw "Pre-packaging gate failed: Required artifact is empty (0 bytes): $rel ($fullPath)"
    }
}
Write-DistLog "Pre-Packaging Integrity Gate PASSED: All $($requiredFiles.Count) required items verified." "SUCCESS"

# --- 3. Build NSIS Installer ---
if (-not $SkipInstaller) {
    if (-not $makensis) {
        throw "NSIS compiler (makensis.exe) not found. Cannot build NSIS setup package."
    }
    Write-DistLog "Compiling NSIS installer using $makensis ..." "INFO"
    $nsiScript = Join-Path $RepoRoot "installer/LocalModelConductor.nsi"

    $cleanVersion = ($Version -replace '^v', '')
    $numericVersion = ($cleanVersion -split '-')[0]
    $nsisArgs = @(
        "/INPUTCHARSET", "UTF8",
        "-DPRODUCT_VERSION=$numericVersion",
        "-DSOURCE_DIR=$fullDist",
        "-DOUTPUT_DIR=$fullRelease",
        "-DAPP_ICON=$iconPath",
        $nsiScript
    )
    & $makensis @nsisArgs
    if ($LASTEXITCODE -ne 0) {
        throw "NSIS installer compilation failed with exit code $LASTEXITCODE."
    }

    $setupExe = Join-Path $fullRelease "LocalModelConductor-Setup-win-x64.exe"
    if (-not (Test-Path -LiteralPath $setupExe)) {
        throw "Expected NSIS installer not produced: $setupExe"
    }
    $setupLen = (Get-Item -LiteralPath $setupExe).Length
    Write-DistLog "NSIS Installer generated successfully: $setupExe ($([math]::Round($setupLen / 1MB, 2)) MB)" "SUCCESS"
}

# --- 4. Build Portable ZIP Package ---
Write-DistLog "Generating Portable ZIP archive..." "INFO"
$zipPath = Join-Path $fullRelease "LocalModelConductor-Portable-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path "$fullDist/*" -DestinationPath $zipPath -CompressionLevel Optimal
$zipLen = (Get-Item -LiteralPath $zipPath).Length
Write-DistLog "Portable ZIP generated successfully: $zipPath ($([math]::Round($zipLen / 1MB, 2)) MB)" "SUCCESS"

# --- 5. Generate SHA-256 Digests and Release Manifest ---
Write-DistLog "Calculating release checksums and building manifest..." "INFO"
$releaseFiles = Get-ChildItem -Path $fullRelease -File | Where-Object { $_.Extension -in '.exe', '.zip' }
$sums = @()
$manifestEntries = @()

foreach ($f in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $($f.Name)"
    $manifestEntries += [ordered]@{
        filename = $f.Name
        sha256 = $hash
        sizeBytes = $f.Length
    }
    Write-DistLog "  Artifact: $($f.Name) | SHA-256: $hash" "INFO"
}

$sumsFile = Join-Path $fullRelease "SHA256SUMS.txt"
Set-Content -LiteralPath $sumsFile -Value ($sums -join "`r`n") -Encoding ascii
Write-DistLog "Wrote SHA256SUMS.txt to $sumsFile" "SUCCESS"

$manifestFile = Join-Path $fullRelease "release-manifest.json"
$manifestObject = [ordered]@{
    version = $Version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    artifacts = $manifestEntries
}
$manifestObject | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestFile -Encoding utf8
Write-DistLog "Wrote release-manifest.json to $manifestFile" "SUCCESS"

Write-DistLog "Distribution build and packaging completed successfully!" "SUCCESS"
