param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'build' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Management.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll', '/reference:Microsoft.CSharp.dll')
$runtimeSources = @('ControlProxy.cs', 'ProxyRuntime.cs', 'ChildLifetime.cs', 'ProxyProcess.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
$launcherSources = @('Program.cs','PackagedApp.cs','PackageLaunch.cs','RelaySession.cs','HostsMapping.cs','HostsTransaction.cs','LaunchLog.cs','NetworkGuard.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
function Invoke-Compile([string]$name, [string]$target, [string[]]$sources, [string[]]$options = @()) {
    $nativeArgs = @('/nologo', "/target:$target", '/optimize+', '/platform:anycpu', "/out:$output\$name") + $references + $options + $sources
    & $compiler @nativeArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $name ($LASTEXITCODE)" }
}
$applicationOptions = @("/win32manifest:$projectRoot\src\app.manifest", "/win32icon:$projectRoot\assets\chatgpt-verge-rainbow-icon.ico")
Invoke-Compile 'ChatGPT-Verge-Launcher.exe' 'winexe' ($launcherSources + $runtimeSources) $applicationOptions
Invoke-Compile 'ChatGPT-Verge-Launcher-Diagnostics.exe' 'exe' ($launcherSources + $runtimeSources) $applicationOptions
Invoke-Compile 'ChatGPT-Verge-ControlProxy.exe' 'exe' ($runtimeSources + (Join-Path $projectRoot 'src\ControlProxyHost.cs'))
Invoke-Compile 'ChatGPT-Verge-NodeProxy.exe' 'exe' ($runtimeSources + (Join-Path $projectRoot 'src\RuntimeProxyHost.cs')) @('/define:NODE_PROXY')
Invoke-Compile 'ChatGPT-Verge-CodexProxy.exe' 'exe' ($runtimeSources + (Join-Path $projectRoot 'src\RuntimeProxyHost.cs'))
Invoke-Compile 'ChatGPT-DurableRelay.exe' 'exe' @((Join-Path $projectRoot 'src\DurableRelay.cs'), (Join-Path $projectRoot 'src\LaunchLog.cs'), (Join-Path $projectRoot 'src\RelayDiagnostics.cs')) @('/reference:System.Runtime.Serialization.dll')
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\proxy-preload.cjs') -Destination $output
Get-ChildItem -LiteralPath $output -File | Select-Object Name,Length
