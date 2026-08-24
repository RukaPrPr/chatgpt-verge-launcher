param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot "src\Program.cs"
$iconPath = Join-Path $projectRoot "assets\chatgpt-verge-rainbow-icon.ico"
$compilerPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw ".NET Framework C# compiler not found: $compilerPath"
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Application icon not found: $iconPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "build"
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$launcherPath = Join-Path $resolvedOutput "ChatGPT-Verge-Launcher.exe"
$diagnosticPath = Join-Path $resolvedOutput "ChatGPT-Verge-Launcher-Diagnostics.exe"

& $compilerPath `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /win32icon:$iconPath `
    /out:$launcherPath `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    $sourcePath

if ($LASTEXITCODE -ne 0) {
    throw "Launcher compilation failed with exit code $LASTEXITCODE"
}

& $compilerPath `
    /nologo `
    /target:exe `
    /optimize+ `
    /platform:anycpu `
    /win32icon:$iconPath `
    /out:$diagnosticPath `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    $sourcePath

if ($LASTEXITCODE -ne 0) {
    throw "Diagnostic compilation failed with exit code $LASTEXITCODE"
}

Get-Item -LiteralPath $launcherPath, $diagnosticPath |
    Select-Object FullName, Length, LastWriteTime

Get-FileHash -Algorithm SHA256 -LiteralPath $launcherPath |
    Select-Object Algorithm, Hash, Path
