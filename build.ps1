param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\public-win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'BDDVD2Flac\BDDVD2Flac.csproj'
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

# This publish mode deliberately excludes FFmpeg and the local embedded-tools bundle.
& $dotnetExe publish $project -c $Configuration -r win-x64 --self-contained false `
    -o $destination '-p:PublishSingleFile=false' '-p:ToolsBundleId=' '-p:DebugType=None'
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
