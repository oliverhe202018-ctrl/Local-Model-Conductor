<#
.SYNOPSIS
    Local Model Conductor (LMC) - One-Click Bootstrapper & Installer
.DESCRIPTION
    PowerShell installation script following the Antigravity-Manager distribution architecture.
    Automates downloading, SHA-256 checksum verification, and launching the official NSIS setup package.
.PARAMETER Version
    Specific SemVer version to install (e.g. "3.0.0" or "v3.0.0"). Defaults to latest stable.
.PARAMETER Channel
    Release channel: "stable" (default) or "preview". Stable ignores pre-release builds.
.PARAMETER DryRun
    When set, only displays planned actions, asset URLs, and checksums without downloading or executing.
.PARAMETER RepoOwner
    GitHub repository owner / organization. Defaults to "oliverhe202018-ctrl".
.PARAMETER RepoName
    GitHub repository name. Defaults to "Local-Model-Conductor".
.PARAMETER LocalManifestPath
    Path to a local release manifest or SHA256SUMS.txt (for offline testing or air-gapped environments).
.PARAMETER LocalInstallerPath
    Path to a pre-downloaded installer file (for local installation and acceptance tests).
.PARAMETER Silent
    Run NSIS installer in silent mode (/S). Defaults to true for automated installations.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet("stable", "preview")]
    [string]$Channel = "stable",
    [switch]$DryRun,
    [string]$RepoOwner = "oliverhe202018-ctrl",
    [string]$RepoName = "Local-Model-Conductor",
    [string]$LocalManifestPath,
    [string]$LocalInstallerPath,
    [switch]$Silent = $true
)

$ErrorActionPreference = "Stop"

function Write-LmcLog {
    param([string]$Message, [string]$Level = "INFO")
    $color = switch ($Level) {
        "INFO" { "Cyan" }
        "WARN" { "Yellow" }
        "ERROR" { "Red" }
        "SUCCESS" { "Green" }
        default { "White" }
    }
    Write-Host "[LMC-INSTALL] [$Level] $Message" -ForegroundColor $color
}

function Test-SemVer {
    param([string]$Ver)
    return $Ver -match '^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$'
}

function Get-ReleaseMetadata {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$TargetVersion,
        [string]$TargetChannel,
        [string]$ManifestOverride
    )

    if (-not [string]::IsNullOrWhiteSpace($ManifestOverride) -and (Test-Path $ManifestOverride)) {
        Write-LmcLog "Loading local release manifest from: $ManifestOverride" "INFO"
        $raw = Get-Content -LiteralPath $ManifestOverride -Raw -Encoding utf8
        if ($ManifestOverride.EndsWith(".json", [System.StringComparison]::OrdinalIgnoreCase)) {
            $manifest = $raw | ConvertFrom-Json
            return [PSCustomObject]@{
                TagName = if ($manifest.version) { $manifest.version } else { "v3.0.0" }
                InstallerAsset = $manifest.installerAsset
                Checksum = $manifest.sha256
                IsPrerelease = [bool]$manifest.isPrerelease
                Source = "LocalManifest"
            }
        }
    }

    # Query GitHub Releases REST API
    $apiUrl = "https://api.github.com/repos/$Owner/$Repo/releases"
    Write-LmcLog "Querying GitHub Releases API: $apiUrl" "INFO"
    
    $headers = @{
        "User-Agent" = "LocalModelConductor-Bootstrapper/1.0"
        "Accept" = "application/vnd.github.v3+json"
    }

    try {
        $response = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get -TimeoutSec 15
    }
    catch {
        $ex = $_.Exception
        if ($DryRun) {
            Write-LmcLog "[DRY-RUN] GitHub repository '$Owner/$Repo' is not yet published or unreachable. Using planned release metadata." "WARN"
            $tag = if ($TargetVersion) { $TargetVersion } else { "v3.0.0" }
            return [PSCustomObject]@{
                TagName = $tag
                InstallerName = "LocalModelConductor-Setup-win-x64.exe"
                DownloadUrl = "https://github.com/$Owner/$Repo/releases/download/$tag/LocalModelConductor-Setup-win-x64.exe"
                ChecksumDownloadUrl = "https://github.com/$Owner/$Repo/releases/download/$tag/SHA256SUMS.txt"
                IsPrerelease = ($TargetChannel -eq "preview")
                Checksum = $null
                Source = "DryRunPlannedTemplate"
            }
        }
        if ($ex.Response -and $ex.Response.StatusCode -eq 403) {
            throw "GitHub API rate limit exceeded. Please retry later or provide -LocalInstallerPath."
        }
        throw "Failed to fetch releases from GitHub: $($ex.Message)"
    }

    $candidates = @()
    foreach ($rel in $response) {
        if ($rel.draft -eq $true) { continue }
        if ($TargetChannel -eq "stable" -and $rel.prerelease -eq $true) { continue }
        $candidates += $rel
    }

    if ($candidates.Count -eq 0) {
        throw "No matching releases found for channel '$TargetChannel' in $Owner/$Repo."
    }

    $selectedRelease = $null
    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $norm = $TargetVersion.TrimStart('v')
        $selectedRelease = $candidates | Where-Object {
            ($_.tag_name.TrimStart('v') -eq $norm) -or ($_.name.TrimStart('v') -eq $norm)
        } | Select-Object -First 1
        if (-not $selectedRelease) {
            throw "Specified version '$TargetVersion' was not found in $Owner/$Repo releases."
        }
    } else {
        $selectedRelease = $candidates | Select-Object -First 1
    }

    $asset = $selectedRelease.assets | Where-Object {
        $_.name -match '^LocalModelConductor-Setup.*\.exe$'
    } | Select-Object -First 1

    if (-not $asset) {
        throw "Release '$($selectedRelease.tag_name)' does not contain a valid NSIS installer asset (LocalModelConductor-Setup-*.exe)."
    }

    $checksumAsset = $selectedRelease.assets | Where-Object {
        $_.name -in @("SHA256SUMS.txt", "release-manifest.json")
    } | Select-Object -First 1

    return [PSCustomObject]@{
        TagName = $selectedRelease.tag_name
        InstallerName = $asset.name
        DownloadUrl = $asset.browser_download_url
        ChecksumDownloadUrl = if ($checksumAsset) { $checksumAsset.browser_download_url } else { $null }
        IsPrerelease = [bool]$selectedRelease.prerelease
        Source = "GitHubReleases"
    }
}

try {
    Write-LmcLog "Local Model Conductor (本地模型调度台) - Release Bootstrapper" "INFO"
    Write-LmcLog "Execution Mode: Channel='$Channel', DryRun=$($DryRun.IsPresent)" "INFO"

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        if (-not (Test-SemVer $Version)) {
            throw "Invalid SemVer format for -Version: '$Version'. Expected format: X.Y.Z or vX.Y.Z"
        }
        Write-LmcLog "Target version specified: $Version" "INFO"
    }

    # Determine Installer Path & Verification Source
    $installerPath = ""
    $expectedHash = ""
    $tagName = ""
    $installerName = "LocalModelConductor-Setup-win-x64.exe"
    $tempWorkDir = ""

    if (-not [string]::IsNullOrWhiteSpace($LocalInstallerPath)) {
        if (-not (Test-Path $LocalInstallerPath)) {
            throw "Specified -LocalInstallerPath does not exist: $LocalInstallerPath"
        }
        $installerPath = [System.IO.Path]::GetFullPath($LocalInstallerPath)
        $installerName = [System.IO.Path]::GetFileName($installerPath)
        $tagName = if ($Version) { $Version } else { "v3.0.0-local" }
        Write-LmcLog "Using local installer file: $installerPath" "INFO"

        # Check for companion hash
        $companionSha = "$installerPath.sha256"
        if (Test-Path $companionSha) {
            $expectedHash = (Get-Content -LiteralPath $companionSha -Raw).Trim().Split(" `t")[0].ToLowerInvariant()
            Write-LmcLog "Loaded companion SHA-256: $expectedHash" "INFO"
        }
    }
    else {
        $meta = Get-ReleaseMetadata -Owner $RepoOwner -Repo $RepoName -TargetVersion $Version -TargetChannel $Channel -ManifestOverride $LocalManifestPath
        $tagName = $meta.TagName
        $installerName = if ($meta.InstallerName) { $meta.InstallerName } else { "LocalModelConductor-Setup-win-x64.exe" }
        $downloadUrl = $meta.DownloadUrl
        $expectedHash = if ($meta.Checksum) { $meta.Checksum } else { "" }

        if ($DryRun) {
            Write-LmcLog "[DRY-RUN] Target Version: $tagName" "INFO"
            Write-LmcLog "[DRY-RUN] Channel: $Channel (Prerelease=$($meta.IsPrerelease))" "INFO"
            Write-LmcLog "[DRY-RUN] Asset Name: $installerName" "INFO"
            Write-LmcLog "[DRY-RUN] Download URL: $downloadUrl" "INFO"
            Write-LmcLog "[DRY-RUN] Expected SHA-256: $(if ($expectedHash) { $expectedHash } else { '<Fetched from SHA256SUMS.txt>' })" "INFO"
            Write-LmcLog "[DRY-RUN] Planned Execution: Start-Process -FilePath <temp>\\$installerName -ArgumentList $(if ($Silent) { '/S' } else { '' }) -Wait" "INFO"
            Write-LmcLog "[DRY-RUN] Dry run completed. No files downloaded, no system changes made." "SUCCESS"
            return
        }

        # Create isolated temporary download sandbox
        $tempWorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("LMC_Install_" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempWorkDir -Force | Out-Null
        $installerPath = Join-Path $tempWorkDir $installerName

        Write-LmcLog "Downloading $installerName from $downloadUrl ..." "INFO"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -TimeoutSec 120
        Write-LmcLog "Download completed: $installerPath" "SUCCESS"

        if (-not $expectedHash -and $meta.ChecksumDownloadUrl) {
            $sumsPath = Join-Path $tempWorkDir "SHA256SUMS.txt"
            Invoke-WebRequest -Uri $meta.ChecksumDownloadUrl -OutFile $sumsPath -TimeoutSec 30
            $lines = Get-Content -LiteralPath $sumsPath
            foreach ($line in $lines) {
                if ($line -match "([a-fA-F0-9]{64})\s+\*?$([regex]::Escape($installerName))") {
                    $expectedHash = $matches[1].ToLowerInvariant()
                    break
                }
            }
        }
    }

    if ($DryRun) {
        Write-LmcLog "[DRY-RUN] Installer file: $installerPath" "INFO"
        Write-LmcLog "[DRY-RUN] Expected hash: $expectedHash" "INFO"
        Write-LmcLog "[DRY-RUN] Planned command: Start-Process -FilePath `"$installerPath`" -ArgumentList $(if ($Silent) { '/S' } else { '' }) -Wait" "INFO"
        Write-LmcLog "[DRY-RUN] Dry run completed successfully." "SUCCESS"
        return
    }

    # SHA-256 Integrity Verification
    if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
        Write-LmcLog "Computing local SHA-256 digest for $installerPath ..." "INFO"
        $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-LmcLog "Calculated hash: $actualHash" "INFO"
        Write-LmcLog "Expected hash:   $expectedHash" "INFO"

        if ($actualHash -ne $expectedHash.ToLowerInvariant()) {
            throw "SHA-256 checksum mismatch! The downloaded installer may be corrupt or tampered with.`nExpected: $expectedHash`nActual:   $actualHash"
        }
        Write-LmcLog "SHA-256 digest successfully verified!" "SUCCESS"
    } else {
        Write-LmcLog "Warning: No checksum provided for verification. Proceeding with caution." "WARN"
    }

    # Execute NSIS Installer
    Write-LmcLog "Launching installer: $installerPath" "INFO"
    $installArgs = if ($Silent) { "/S" } else { "" }
    $proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru

    Write-LmcLog "Installer process exited with code: $($proc.ExitCode)" "INFO"
    if ($proc.ExitCode -ne 0) {
        throw "Installer exited with non-zero exit code: $($proc.ExitCode). Installation failed."
    }

    Write-LmcLog "Local Model Conductor (本地模型调度台) installed successfully!" "SUCCESS"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($tempWorkDir) -and (Test-Path $tempWorkDir)) {
        try {
            Remove-Item -LiteralPath $tempWorkDir -Recurse -Force -ErrorAction SilentlyContinue
        } catch { }
    }
}
