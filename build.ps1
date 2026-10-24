param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\public-win-x64',
    [string[]]$PackageSources = @()
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'DiscChannelLab.App\DiscChannelLab.App.csproj'
$destination = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $projectRoot $OutputDirectory
}
$sdkCandidates = @(
    (Join-Path (Split-Path -Parent $projectRoot) '.dotnet10\sdk\dotnet.exe'),
    (Join-Path (Split-Path -Parent (Split-Path -Parent $projectRoot)) '.dotnet10\sdk\dotnet.exe')
)
$localDotnet = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$dotnetExe = if ($null -ne $localDotnet) {
    $localDotnet
} else {
    (Get-Command 'dotnet.exe' -ErrorAction Stop).Source
}
$sdkVersion = & $dotnetExe --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw ".NET 10 SDK is required. Found: $dotnetExe ($sdkVersion)"
}
foreach ($name in @('phonon.dll', 'LICENSE.txt', 'THIRDPARTY.md', 'provenance.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "tools\steamaudio\$name") -PathType Leaf)) {
        throw 'Run python fetch-steamaudio.py to prepare the Steam Audio runtime and notices.'
    }
}

# This publish mode deliberately excludes FFmpeg and the local embedded-tools bundle.
$restoreArguments = @()
foreach ($source in $PackageSources) { $restoreArguments += @('--source', $source) }
& $dotnetExe publish $project -c $Configuration -r win-x64 --self-contained false `
    -o $destination '-p:PublishSingleFile=false' '-p:ToolsBundleId=' '-p:DebugType=None' @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Public build failed.' }

# An earlier local build may have left a symbols file containing local paths.
$symbols = Join-Path $destination 'DiscChannelLab.pdb'
if (Test-Path -LiteralPath $symbols -PathType Leaf) { Remove-Item -LiteralPath $symbols }

$unexpectedTools = @('ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe') |
    Where-Object { Test-Path -LiteralPath (Join-Path $destination $_) -PathType Leaf }
if ($unexpectedTools.Count -gt 0) {
    throw "Public output unexpectedly contains FFmpeg tools: $($unexpectedTools -join ', ')"
}
Write-Output "Public build complete: $destination"

# The viewer is a separate process, auto-detected beside the player in SpaceSketch/.
$viewerDestination = Join-Path $destination 'SpaceSketch'
& $dotnetExe publish (Join-Path $projectRoot 'DiscChannelLab.Visualizer\DiscChannelLab.Visualizer.csproj') `
    -c $Configuration -r win-x64 --self-contained false -o $viewerDestination '-p:DebugType=None' @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Visualizer build failed.' }
$analysisSettings = Join-Path $viewerDestination 'AudioVisualizationSettings.json'
if (-not (Test-Path -LiteralPath $analysisSettings)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'DiscChannelLab.Visualizer\AudioVisualizationSettings.json') -Destination $analysisSettings
}
Write-Output "Visualizer build complete: $viewerDestination"
foreach ($name in @('README.md', 'README.ja.md', 'LICENSE', 'NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $destination -Recurse -Force
$thirdParty = Join-Path $destination 'third_party'
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
foreach ($name in @('PureHDF','libadm-common-definitions')) { Copy-Item -LiteralPath (Join-Path $projectRoot "third_party/$name") -Destination $thirdParty -Recurse -Force }
$forbidden = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
    $_.Name -in @('ffmpeg.exe','ffprobe.exe','ffplay.exe','truehdd.exe','sacd_extract.exe','prepared-adm-library.json') -or
    $_.Extension.ToLowerInvariant() -in @('.sofa','.thd','.caf','.m2ts','.flac','.wav','.f32','.pdb')
}
if ($forbidden) { throw ('Unexpected external tools, private data or symbols in public output: ' + ($forbidden.Name -join ', ')) }
