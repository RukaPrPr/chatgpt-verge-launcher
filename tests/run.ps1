param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$TestDirectory)
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
