param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$TestDirectory,[switch]$InstalledPackage)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = [IO.Path]::GetFullPath($BuildDirectory)
$test = [IO.Path]::GetFullPath($TestDirectory)
New-Item -ItemType Directory -Path $test -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'ChatGPT-Verge-ControlProxy.exe') -Destination $test
[IO.File]::WriteAllText((Join-Path $test 'node.exe'), 'Test path marker; never executed')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$test\node_repl.exe" (Join-Path $PSScriptRoot 'RuntimeProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed' }
& $compiler /nologo /target:exe "/out:$test\IntegrationTests.exe" (Join-Path $PSScriptRoot 'IntegrationTests.cs') (Join-Path $root 'src\ControlProxy.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& (Join-Path $test 'IntegrationTests.exe') $test
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
$packageSources = @(
    (Join-Path $PSScriptRoot 'PackageTests.cs'),
    (Join-Path $PSScriptRoot 'PackageProcessProbe.cs'),
    (Join-Path $root 'src\Program.cs'),
    (Join-Path $root 'src\PackagedApp.cs'),
    (Join-Path $root 'src\PackageLaunch.cs'),
    (Join-Path $root 'src\ControlProxy.cs')
)
& $compiler /nologo /target:exe /main:PackageTests "/out:$test\PackageTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll @packageSources
if ($LASTEXITCODE -ne 0) { throw 'Package test compilation failed' }
$testArguments = @($test)
if ($InstalledPackage) { $testArguments += '--installed-package' }
& (Join-Path $test 'PackageTests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Package tests failed' }
