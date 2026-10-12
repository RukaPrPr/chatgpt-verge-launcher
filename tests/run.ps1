param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$TestDirectory,[switch]$InstalledPackage)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = [IO.Path]::GetFullPath($BuildDirectory)
$test = [IO.Path]::GetFullPath($TestDirectory)
New-Item -ItemType Directory -Path $test -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'ChatGPT-Verge-ControlProxy.exe') -Destination $test
foreach ($name in @('ChatGPT-Verge-NodeProxy.exe','ChatGPT-Verge-CodexProxy.exe','proxy-preload.cjs')) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination $test }
[IO.File]::WriteAllText((Join-Path $test 'node.exe'), 'Test path marker; never executed')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$proxySources = @((Join-Path $root 'src\ControlProxy.cs'), (Join-Path $root 'src\ProxyRuntime.cs'))
$guardSources = $proxySources + (Join-Path $root 'src\NetworkGuard.cs')
$guardReferences = @('/reference:System.Web.Extensions.dll', '/reference:Microsoft.CSharp.dll')
& $compiler /nologo /target:exe "/out:$test\node_repl.exe" (Join-Path $PSScriptRoot 'RuntimeProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed' }
& $compiler /nologo /target:exe "/out:$test\IntegrationTests.exe" @guardReferences (Join-Path $PSScriptRoot 'IntegrationTests.cs') @proxySources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& (Join-Path $test 'IntegrationTests.exe') $test
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
$packageSources = @(
    (Join-Path $PSScriptRoot 'PackageTests.cs'),
    (Join-Path $PSScriptRoot 'PackageProcessProbe.cs'),
    (Join-Path $root 'src\Program.cs'),
    (Join-Path $root 'src\PackagedApp.cs'),
    (Join-Path $root 'src\PackageLaunch.cs'),
    (Join-Path $root 'src\ControlProxy.cs'),
    (Join-Path $root 'src\ProxyRuntime.cs'),
    (Join-Path $root 'src\NetworkGuard.cs'),
    (Join-Path $root 'src\LaunchLog.cs'),
    (Join-Path $root 'src\HostsTransaction.cs'),
    (Join-Path $root 'src\HostsMapping.cs')
    (Join-Path $root 'src\RelaySession.cs')
)
& $compiler /nologo /target:exe /main:PackageTests "/out:$test\PackageTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll @guardReferences @packageSources
if ($LASTEXITCODE -ne 0) { throw 'Package test compilation failed' }
$testArguments = @($test)
if ($InstalledPackage) { $testArguments += '--installed-package' }
& (Join-Path $test 'PackageTests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Package tests failed' }
$relayTestSources = @(
    (Join-Path $PSScriptRoot 'RelayTests.cs'),
    (Join-Path $root 'src\LaunchLog.cs'),
    (Join-Path $root 'src\HostsTransaction.cs'),
    (Join-Path $root 'src\HostsMapping.cs'),
    (Join-Path $root 'src\RelaySession.cs'),
    (Join-Path $root 'src\PackageLaunch.cs'),
    (Join-Path $root 'src\PackagedApp.cs')
)
$relayTestSources += $guardSources
& $compiler /nologo /target:exe "/out:$test\RelayTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Windows.Forms.dll @guardReferences @relayTestSources
if ($LASTEXITCODE -ne 0) { throw 'Relay test compilation failed' }
& (Join-Path $test 'RelayTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Relay tests failed' }
$recoverySources = @($relayTestSources | Where-Object { $_ -notlike '*RelayTests.cs' }) + (Join-Path $PSScriptRoot 'RecoveryTests.cs')
& $compiler /nologo /target:exe "/out:$test\RecoveryTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Windows.Forms.dll @guardReferences @recoverySources
if ($LASTEXITCODE -ne 0) { throw 'Recovery test compilation failed' }
& (Join-Path $test 'RecoveryTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Recovery tests failed' }
& $compiler /nologo /target:exe "/out:$test\LaunchLogTests.exe" /reference:System.dll /reference:System.Core.dll (Join-Path $PSScriptRoot 'LaunchLogTests.cs') (Join-Path $root 'src\LaunchLog.cs')
if ($LASTEXITCODE -ne 0) { throw 'Logging test compilation failed' }
& (Join-Path $test 'LaunchLogTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Logging tests failed' }
