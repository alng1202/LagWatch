param(
    # Production exe (LagWatch.exe) without the developer tier selector. Default is the dev build (LagWatch-dev.exe).
    [switch]$MainBuild,
    # Framework-dependent exe (~1 MB, needs the .NET 8 Desktop Runtime) instead of a self-contained single file.
    [switch]$Small,
    # Skip the built-in self-tests after building.
    [switch]$NoTest
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'LagWatch.csproj'

# LagWatch + NetFix needs the .NET 8 SDK (NetFix's engine uses C# 12 and WinRT). Use a per-user install if present.
$dotnetCandidates = @(
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    'C:\Program Files\dotnet\dotnet.exe'
)
$dotnet = $null
foreach ($candidate in $dotnetCandidates) {
    if ((Test-Path -LiteralPath $candidate) -and ((& $candidate --list-sdks) -match '^8\.')) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    throw 'The .NET 8 SDK was not found. Install it, or per user with: dotnet-install.ps1 -Channel 8.0'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# App icon, drawn from code (unchanged from the original LagWatch build).
$iconPath = Join-Path $projectRoot 'LagWatch.ico'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(10, 16, 27))
$accentBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(44, 209, 190))
$linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(44, 209, 190)), 6
$linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.FillEllipse($accentBrush, 7, 7, 10, 10)
$points = [System.Drawing.Point[]]@(
    (New-Object System.Drawing.Point 10, 38),
    (New-Object System.Drawing.Point 20, 38),
    (New-Object System.Drawing.Point 26, 22),
    (New-Object System.Drawing.Point 34, 49),
    (New-Object System.Drawing.Point 42, 31),
    (New-Object System.Drawing.Point 54, 31)
)
$graphics.DrawLines($linePen, $points)
$handle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($handle)
$stream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
try { $icon.Save($stream) } finally { $stream.Dispose() }
$icon.Dispose()
$linePen.Dispose()
$accentBrush.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

$exeName = $(if ($MainBuild) { 'LagWatch.exe' } else { 'LagWatch-dev.exe' })
$outputPath = Join-Path $projectRoot $exeName
$publishDir = Join-Path $projectRoot ("bin\publish\" + $(if ($MainBuild) { 'main' } else { 'dev' }))
$mainFlag = $(if ($MainBuild) { 'true' } else { 'false' })

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir,
    "-p:LagWatchMainBuild=$mainFlag",
    '-p:PublishSingleFile=true',
    '-p:DebugType=none',
    '-nologo', '-v', 'q'
)
if ($Small) {
    $arguments += '--self-contained', 'false'
} else {
    $arguments += '--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
}

& $dotnet $arguments
if ($LASTEXITCODE -ne 0) {
    throw "LagWatch build failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $publishDir 'LagWatch.exe') -Destination $outputPath -Force
$file = Get-Item -LiteralPath $outputPath
Write-Host "Built $($file.FullName) ($([Math]::Round($file.Length / 1MB, 1)) MB)"

if (-not $NoTest) {
    $report = Join-Path $projectRoot 'self-test-report.txt'
    $process = Start-Process -FilePath $outputPath -ArgumentList '--self-test', '--report', "`"$report`"" -Wait -PassThru
    Get-Content -LiteralPath $report | Write-Host
    if ($process.ExitCode -ne 0) { throw 'LagWatch self-tests failed.' }
}
