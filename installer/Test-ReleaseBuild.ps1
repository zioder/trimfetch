# Local smoke test for Release workflow (publish + Inno Setup for x64 and arm64).
# Requires: .NET 10 SDK, Inno Setup 6 (https://jrsoftware.org/isinfo.php)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repoRoot 'src/TrimFetch/TrimFetch.csproj'
$semver = '1.0.0-test'
$artifacts = Join-Path $repoRoot 'artifacts'
$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install: winget install JRSoftware.InnoSetup"
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path $artifacts) {
    Get-ChildItem $artifacts -Filter 'TrimFetchSetup-*' -File | Remove-Item -Force
}

$targets = @(
    @{ Platform = 'x64'; Rid = 'win-x64'; Arch = 'x64' },
    @{ Platform = 'ARM64'; Rid = 'win-arm64'; Arch = 'arm64' }
)

foreach ($target in $targets) {
    Write-Host "=== $($target.Arch) ===" -ForegroundColor Cyan
    dotnet publish $project `
        -c Release `
        -p:Platform=$($target.Platform) `
        -p:WindowsPackageType=None `
        -r $($target.Rid) `
        --self-contained true

    $candidates = @(
        (Join-Path $repoRoot "src/TrimFetch/bin/$($target.Platform)/Release/net10.0-windows10.0.26100.0/$($target.Rid)/publish"),
        (Join-Path $repoRoot "src/TrimFetch/bin/Release/net10.0-windows10.0.26100.0/$($target.Rid)/publish")
    )
    $publishDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'TrimFetch.exe') } | Select-Object -First 1
    if (-not $publishDir) { throw "Publish output not found for $($target.Arch)." }

    $publishDir = (Resolve-Path -LiteralPath $publishDir).Path
    $outputDir = (Resolve-Path -LiteralPath $artifacts).Path

    & $iscc (Join-Path $repoRoot 'installer/TrimFetch.iss') `
        "/DPublishDir=$publishDir" `
        "/DMyAppVersion=$semver" `
        "/DTargetArch=$($target.Arch)" `
        "/DOutputDir=$outputDir"

    $setup = Join-Path $artifacts "TrimFetchSetup-$semver-$($target.Arch).exe"
    if (-not (Test-Path $setup)) { throw "Missing $setup" }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "OK $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB) sha256:$hash" -ForegroundColor Green
}

Write-Host "`nAll installers built successfully." -ForegroundColor Green
