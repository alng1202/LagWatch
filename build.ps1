param(
    [switch]$MainBuild
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

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

$sourceFiles = @(
    (Join-Path $projectRoot 'AssemblyInfo.cs'),
    (Join-Path $projectRoot 'Program.cs'),
    (Join-Path $projectRoot 'Models.cs'),
    (Join-Path $projectRoot 'OutageMap.cs'),
    (Join-Path $projectRoot 'Theme.cs'),
    (Join-Path $projectRoot 'LatencyChart.cs'),
    (Join-Path $projectRoot 'MainForm.cs')
)
$outputPath = Join-Path $projectRoot $(if ($MainBuild) { 'LagWatch.exe' } else { 'LagWatch-dev.exe' })
$manifestPath = Join-Path $projectRoot 'app.manifest'

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/debug-',
    "/out:$outputPath",
    "/win32icon:$iconPath",
    "/win32manifest:$manifestPath",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
) + $sourceFiles

if (-not $MainBuild) {
    $arguments += '/define:DEV_BUILD'
}

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "LagWatch build failed with compiler exit code $LASTEXITCODE."
}

$file = Get-Item -LiteralPath $outputPath
Write-Host "Built $($file.FullName) ($([Math]::Round($file.Length / 1KB, 1)) KB)"
