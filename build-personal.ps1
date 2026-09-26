param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts\standalone-win-x64'
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
$ffmpeg = Get-Command 'ffmpeg.exe' -ErrorAction SilentlyContinue
$ffprobe = Get-Command 'ffprobe.exe' -ErrorAction SilentlyContinue
$ffplay = Get-Command 'ffplay.exe' -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg -or $null -eq $ffprobe -or $null -eq $ffplay) {
    throw '単一 EXE の作成には ffmpeg.exe、ffprobe.exe、ffplay.exe が必要です。'
}
$protocols = & $ffmpeg.Source -hide_banner -protocols
$decoders = & $ffmpeg.Source -hide_banner -decoders
if (($protocols -join "`n") -notmatch 'bluray' -or ($decoders -join "`n") -notmatch 'pcm_bluray' -or
    ($decoders -join "`n") -notmatch '\bmlp\b' -or ($decoders -join "`n") -notmatch 'pcm_dvd' -or
    ($decoders -join "`n") -notmatch '\bdca\b') {
    throw 'FFmpeg に Blu-ray / DVD-Audio 用デコーダーがありません。'
}
$demuxers = & $ffmpeg.Source -hide_banner -demuxers
if (($demuxers -join "`n") -notmatch 'dvdvideo') { throw 'FFmpeg に DVD-Video デマルチプレクサーがありません。' }
$probeProtocols = & $ffprobe.Source -hide_banner -protocols
if (($probeProtocols -join "`n") -notmatch 'bluray') { throw 'ffprobe に bluray プロトコルがありません。' }

New-Item -ItemType Directory -Path $bundleDirectory -Force | Out-Null
# The compressed bundle is embedded as a resource and extracted to LocalAppData on first launch.
Compress-Archive -LiteralPath $ffmpeg.Source, $ffprobe.Source, $ffplay.Source -DestinationPath $bundlePath -CompressionLevel Optimal -Force
$bundleId = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()
& $dotnetExe restore $project -r win-x64 -p:SelfContained=true --source $offlineSource
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore に失敗しました。' }
& $dotnetExe publish $project -c $Configuration --no-restore -r win-x64 -o $destination `
    -p:PublishSingleFile=true -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    "-p:ToolsBundleId=$bundleId"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました。' }
Write-Output "単一 EXE ビルド完了 (net10.0-windows, SDK $sdkVersion): $(Join-Path $destination 'DiscChannelLab.exe')"
