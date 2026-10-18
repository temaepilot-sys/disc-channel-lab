param(
    [string]$ViewerDirectory = 'artifacts/public-win-x64/SpaceSketch',
    [switch]$Browser
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $candidates = @(
        (Join-Path (Split-Path -Parent $root) '.dotnet10/sdk/dotnet.exe'),
        (Join-Path (Split-Path -Parent (Split-Path -Parent $root)) '.dotnet10/sdk/dotnet.exe')
    )
    $sdk = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $sdk) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
    if ((& $sdk --version) -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
    $env:DOTNET_ROOT = Split-Path -Parent $sdk
    $out = Join-Path $root 'verification-output/space-sketch'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $project = 'DiscChannelLab.Visualizer.Verify/DiscChannelLab.Visualizer.Verify.csproj'
    $buildArguments = @('build', $project, '-c', 'Release')
    if (Test-Path -LiteralPath 'offline-packages') { $buildArguments += @('--source', 'offline-packages') }
    & $sdk @buildArguments
    if ($LASTEXITCODE) { throw 'Verification build failed.' }
    $verify = Join-Path $root 'DiscChannelLab.Visualizer.Verify/bin/Release/net10.0-windows/DiscChannelLab.Visualizer.Verify.dll'
    $viewer = Join-Path $root $ViewerDirectory
    & $sdk (Join-Path $viewer 'DiscChannelLab.Visualizer.dll') --self-test > (Join-Path $out 'self-tests.txt')
    if ($LASTEXITCODE) { throw 'Viewer self-tests failed.' }
    & $sdk $verify --host (Join-Path $viewer 'DiscChannelLab.Visualizer.exe') > (Join-Path $out 'host-tests.txt')
    if ($LASTEXITCODE) { throw 'Host / PCM pipe checks failed.' }
    & $sdk $verify --reopen-tests (Join-Path $out 'window-reopen') > (Join-Path $out 'window-reopen.txt')
    if ($LASTEXITCODE) { throw 'Native window reopen checks failed.' }
    & $sdk $verify --write-demo (Join-Path $root 'verification-output/visualizer-demo.packet')
    if ($LASTEXITCODE) { throw 'Demo fixture generation failed.' }
    $modes = @('', 'LAB_SPARK_TESTS', 'LAB_HYBRID_TESTS', 'LAB_CAMERA_NAVIGATION', 'LAB_AURORA_SPARK')
    foreach ($mode in $modes) {
        if ($mode) { Set-Item -LiteralPath "Env:$mode" -Value '1' }
        try {
            $label = if ($mode) { $mode.ToLowerInvariant() } else { 'particles' }
            node tests/visualizer-frontend.cjs > (Join-Path $out "$label.txt")
            if ($LASTEXITCODE) { throw "Frontend checks failed: $label" }
        } finally { if ($mode) { Remove-Item -LiteralPath "Env:$mode" } }
    }
    node tests/hybrid-checks.cjs > (Join-Path $out 'band-motion.txt')
    if ($LASTEXITCODE) { throw 'Band-motion checks failed.' }
    foreach ($failure in @('LAB_SPARK_SHADER_FAILURE', 'LAB_HYBRID_SHADER_FAILURE')) {
        Set-Item -LiteralPath "Env:$failure" -Value '1'
        $env:LAB_AURORA_SPARK = '1'
        try {
            node tests/visualizer-frontend.cjs > (Join-Path $out "$($failure.ToLowerInvariant()).txt")
            if ($LASTEXITCODE) { throw "Shader-fallback checks failed: $failure" }
        } finally { Remove-Item -LiteralPath "Env:$failure"; Remove-Item Env:LAB_AURORA_SPARK }
    }
    if ($Browser) {
        $env:LAB_VIEWER_EXE = Join-Path $viewer 'DiscChannelLab.Visualizer.exe'
        try {
            node tests/camera-home-browser.cjs > (Join-Path $out 'camera-home.txt')
            if ($LASTEXITCODE) { throw 'Actual Home / WebGL checks failed.' }
            node tests/aurora-spark-browser.cjs > (Join-Path $out 'aurora-spark.txt')
            if ($LASTEXITCODE) { throw 'Actual combined-renderer / PCM checks failed.' }
        } finally { Remove-Item Env:LAB_VIEWER_EXE }
    }
    Write-Output "Space sketch checks passed. Results: $out"
} finally { Pop-Location }
