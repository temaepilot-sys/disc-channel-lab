param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\personal-win-x64',
    [string[]]$PackageSources = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'DiscChannelLab.App\DiscChannelLab.App.csproj'
$destination = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
$offlineSource = Join-Path $root 'offline-packages'
$bundleDirectory = Join-Path $root 'DiscChannelLab.App\obj\embedded-tools'
$bundlePath = Join-Path $bundleDirectory 'tools.bundle.zip'
$sdkCandidates = @(
    (Join-Path (Split-Path -Parent $root) '.dotnet10\sdk\dotnet.exe'),
    (Join-Path (Split-Path -Parent (Split-Path -Parent $root)) '.dotnet10\sdk\dotnet.exe')
)
$localDotnet = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$dotnetExe = if ($null -ne $localDotnet) {
    $localDotnet
} else {
    (Get-Command 'dotnet.exe' -ErrorAction Stop).Source
}
$sdkVersion = & $dotnetExe --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw ".NET 10 SDK が必要です。使用した dotnet: $dotnetExe ($sdkVersion)"
}
foreach ($name in @('phonon.dll', 'LICENSE.txt', 'THIRDPARTY.md', 'provenance.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "tools\steamaudio\$name") -PathType Leaf)) {
        throw 'Run python fetch-steamaudio.py to prepare the Steam Audio runtime and notices.'
    }
}

New-Item -ItemType Directory -Path $offlineSource -Force | Out-Null
$runtimeVersion = Get-ChildItem (Join-Path (Split-Path -Parent $dotnetExe) 'shared\Microsoft.NETCore.App') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty Name
if (-not $runtimeVersion) { throw '.NET 10 の実行環境が SDK 内にありません。' }
$requiredPacks = @('microsoft.netcore.app.runtime.win-x64',
    'microsoft.windowsdesktop.app.runtime.win-x64',
    'microsoft.aspnetcore.app.runtime.win-x64') |
    ForEach-Object { Join-Path $offlineSource "$($_).$runtimeVersion.nupkg" }
if ($requiredPacks | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }) {
    python (Join-Path $root 'fetch-runtime-packs.py') $runtimeVersion $offlineSource
    if ($LASTEXITCODE -ne 0) { throw '.NET 10 実行パックの取得に失敗しました。' }
}
# Prefer user-supplied tools in the local tools/ffmpeg/bin directory.
$localTools = Join-Path $root 'tools\ffmpeg\bin'
if (Test-Path -LiteralPath (Join-Path $localTools 'ffmpeg.exe')) {
    $env:PATH = $localTools + [IO.Path]::PathSeparator + $env:PATH
}
$ffmpeg = Get-Command 'ffmpeg.exe' -ErrorAction SilentlyContinue
$ffprobe = Get-Command 'ffprobe.exe' -ErrorAction SilentlyContinue
$ffplay = Get-Command 'ffplay.exe' -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg -or $null -eq $ffprobe -or $null -eq $ffplay) {
    throw '個人用ビルドには ffmpeg.exe、ffprobe.exe、ffplay.exe が必要です。'
}
$protocols = & $ffmpeg.Source -hide_banner -protocols
$decoders = & $ffmpeg.Source -hide_banner -decoders
if (($protocols -join "`n") -notmatch 'bluray' -or ($decoders -join "`n") -notmatch 'pcm_bluray' -or
    ($decoders -join "`n") -notmatch '\bmlp\b' -or ($decoders -join "`n") -notmatch 'pcm_dvda' -or
    ($decoders -join "`n") -notmatch '\bdca\b') {
    throw 'FFmpeg に Blu-ray / DVD-Audio 用デコーダーがありません。'
}
$demuxers = & $ffmpeg.Source -hide_banner -demuxers
if (($demuxers -join "`n") -notmatch 'dvdvideo') { throw 'FFmpeg に DVD-Video デマルチプレクサーがありません。' }
$probeProtocols = & $ffprobe.Source -hide_banner -protocols
if (($probeProtocols -join "`n") -notmatch 'bluray') { throw 'ffprobe に bluray プロトコルがありません。' }

New-Item -ItemType Directory -Path $bundleDirectory -Force | Out-Null
# The compressed bundle is embedded as a resource and extracted to LocalAppData on first launch.
$reuseBundle = $false
if (Test-Path -LiteralPath $bundlePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        $reuseBundle = $archive.Entries.Count -eq 3
        foreach ($tool in @($ffmpeg, $ffprobe, $ffplay)) {
            $entry = $archive.GetEntry([IO.Path]::GetFileName($tool.Source))
            if (!$entry) { $reuseBundle = $false; break }
            $stream = $entry.Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $embeddedHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
            finally { $algorithm.Dispose(); $stream.Dispose() }
            if ($embeddedHash -ne (Get-FileHash -LiteralPath $tool.Source -Algorithm SHA256).Hash) { $reuseBundle = $false; break }
        }
    } finally { $archive.Dispose() }
}
if (!$reuseBundle) { Compress-Archive -LiteralPath $ffmpeg.Source, $ffprobe.Source, $ffplay.Source -DestinationPath $bundlePath -CompressionLevel Optimal -Force }
$bundleId = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()
if ($PackageSources.Count -eq 0) { $PackageSources = @($offlineSource, 'https://api.nuget.org/v3/index.json') }
$restoreArguments = @()
foreach ($source in $PackageSources) { $restoreArguments += @('--source', $source) }
& $dotnetExe restore $project -r win-x64 -p:SelfContained=true @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore に失敗しました。' }
& $dotnetExe publish $project -c $Configuration --no-restore -r win-x64 -o $destination `
    -p:PublishSingleFile=false -p:SelfContained=true -p:DebugType=None `
    "-p:ToolsBundleId=$bundleId"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました。' }
$viewerDestination = Join-Path $destination 'SpaceSketch'
& $dotnetExe publish (Join-Path $root 'DiscChannelLab.Visualizer\DiscChannelLab.Visualizer.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $viewerDestination --source $offlineSource `
    -p:PublishSingleFile=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Visualizer publish failed.' }
$analysisSettings = Join-Path $viewerDestination 'AudioVisualizationSettings.json'
if (-not (Test-Path -LiteralPath $analysisSettings)) {
    Copy-Item -LiteralPath (Join-Path $root 'DiscChannelLab.Visualizer\AudioVisualizationSettings.json') -Destination $analysisSettings
}
foreach ($name in @('README.md', 'README.ja.md', 'LICENSE', 'NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $destination -Recurse -Force
$thirdParty = Join-Path $destination 'third_party'
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
foreach ($name in @('PureHDF','libadm-common-definitions')) { Copy-Item -LiteralPath (Join-Path $root "third_party/$name") -Destination $thirdParty -Recurse -Force }
# Optional, locally supplied object decoder and room presets are personal resources only.
foreach ($folder in @('truehdd','brir-profiles')) {
    $source = Join-Path $root "tools/$folder"
    if (Test-Path -LiteralPath $source) {
        $target = if ($folder -eq 'truehdd') { Join-Path $destination 'tools/truehdd' } else { Join-Path $destination 'Presets/BRIR' }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $source -File) { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
    }
}
Write-Output "個人用ビルド完了 (.NET同梱、フォルダー全体を保持): $(Join-Path $destination 'DiscChannelLab.exe')"
