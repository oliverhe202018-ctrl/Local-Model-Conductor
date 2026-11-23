param(
  [string] $DestinationDir = "tests/fixtures"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TargetDir = Join-Path $RepoRoot $DestinationDir
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$llamaSwapExe = Join-Path $TargetDir "llama-swap.exe"
$fakeModelA = Join-Path $TargetDir "fake-model-a.exe"
$fakeModelB = Join-Path $TargetDir "fake-model-b.exe"

# 1. Official mostlygeek/llama-swap v275 release binary with strict SHA-256 verification
if (-not (Test-Path -LiteralPath $llamaSwapExe -PathType Leaf)) {
    $zipUrl = "https://github.com/mostlygeek/llama-swap/releases/download/v275/llama-swap_275_windows_amd64.zip"
    $expectedSha256 = "00acc3fb5ef0a9b0408ef2ef41000beee2cc8fab074eaf33b91e2cd090450249"
    $zipPath = Join-Path $TargetDir "llama-swap_275_windows_amd64.zip"

    Write-Host "Fetching verified llama-swap v275 from $zipUrl..."
    curl.exe -sL $zipUrl -o $zipPath
    $actualSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        throw "llama-swap SHA256 mismatch! Expected: $expectedSha256, Actual: $actualSha256"
    }
    Write-Host "llama-swap v275 SHA-256 verified successfully: $actualSha256"

    tar.exe -xf $zipPath -C $TargetDir llama-swap.exe
    Remove-Item $zipPath -Force
}

# 2. Build official fake-model from mostlygeek/llama-swap cmd/fake-model
if (-not (Test-Path -LiteralPath $fakeModelA -PathType Leaf) -or -not (Test-Path -LiteralPath $fakeModelB -PathType Leaf)) {
    $go = (Get-Command go -CommandType Application -ErrorAction SilentlyContinue).Source
    if (-not $go) {
        $fallbackGo = "D:\project\本地模型计算\LocalLLMManager\tools\toolchains\go1.27.4\go\bin\go.exe"
        if (Test-Path -LiteralPath $fallbackGo) { $go = $fallbackGo }
    }
    if (-not $go) {
        throw "Go compiler was not found. Please install Go or run actions/setup-go in CI."
    }

    $localSource = "D:\project\本地模型计算\LocalLLMManager\tools\src\llama-swap-275"
    if (Test-Path -LiteralPath (Join-Path $localSource "cmd\fake-model\main.go")) {
        Write-Host "Building fake-model from local toolchain source using $go..."
        & $go build -C $localSource -o $fakeModelA ./cmd/fake-model
    } else {
        $tempClone = Join-Path ([System.IO.Path]::GetTempPath()) ("llama-swap-src-" + [Guid]::NewGuid().ToString("N"))
        Write-Host "Cloning mostlygeek/llama-swap tag v275 for fake-model fixture..."
        & git clone --depth 1 --branch v275 https://github.com/mostlygeek/llama-swap.git $tempClone
        try {
            Write-Host "Compiling fake-model from cloned source using $go..."
            Push-Location $tempClone
            & $go build -o $fakeModelA ./cmd/fake-model
            Pop-Location
        } finally {
            Remove-Item $tempClone -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Copy-Item $fakeModelA $fakeModelB -Force
}

if (-not (Test-Path -LiteralPath $llamaSwapExe) -or -not (Test-Path -LiteralPath $fakeModelA) -or -not (Test-Path -LiteralPath $fakeModelB)) {
    throw "Failed to prepare all required test fixtures in $TargetDir"
}

Write-Host "Test fixtures successfully prepared in: $TargetDir"
Write-Host "  llama-swap: $llamaSwapExe"
Write-Host "  fake-model-a: $fakeModelA"
Write-Host "  fake-model-b: $fakeModelB"

# Export environment variables for GitHub Actions runner if applicable
if ($env:GITHUB_ENV) {
    "LLAMA_SWAP_PATH=$llamaSwapExe" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "FAKE_MODEL_A_PATH=$fakeModelA" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "FAKE_MODEL_B_PATH=$fakeModelB" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "GUARD_TEST_BASE_PORT=19350" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
