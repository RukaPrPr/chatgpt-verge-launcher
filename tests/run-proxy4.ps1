param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$TestDirectory,[Parameter(Mandatory=$true)][string]$PackageExecutable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = [IO.Path]::GetFullPath($TestDirectory)
[void][IO.Directory]::CreateDirectory($test)
[void][IO.Directory]::CreateDirectory((Join-Path $test 'fixture'))
Get-ChildItem -LiteralPath $BuildDirectory -File | Where-Object { $_.Extension -in '.exe','.cjs' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:Microsoft.CSharp.dll')
$sources = @('ControlProxy.cs','ProxyRuntime.cs','NetworkGuard.cs','RelaySession.cs','LaunchLog.cs','HostsMapping.cs','HostsTransaction.cs','PackageLaunch.cs','PackagedApp.cs') | ForEach-Object { Join-Path $root "src\$_" }
& $compiler /nologo /target:exe "/out:$test\GuardTests.exe" @references @sources (Join-Path $PSScriptRoot 'GuardTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Guard test compilation failed' }
& (Join-Path $test 'GuardTests.exe') $test $PackageExecutable
if ($LASTEXITCODE -ne 0) { throw 'Guard tests failed' }
& $compiler /nologo /target:exe "/out:$test\fixture\codex.exe" (Join-Path $PSScriptRoot 'CliProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'CLI probe compilation failed' }
& $compiler /nologo /target:exe "/out:$test\RuntimeProxyTests.exe" @references (Join-Path $PSScriptRoot 'RuntimeProxyTests.cs') (Join-Path $root 'src\ControlProxy.cs') (Join-Path $root 'src\ProxyRuntime.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime proxy test compilation failed' }
& (Join-Path $test 'RuntimeProxyTests.exe') $test
if ($LASTEXITCODE -ne 0) { throw 'Runtime proxy tests failed' }
