param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $test) { throw 'Use a new isolated test directory.' }
[void][IO.Directory]::CreateDirectory($test)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /utf8output /target:exe "/out:$test\RelayDiagnosticsTests.exe" /reference:System.dll /reference:System.Core.dll (Join-Path $PSScriptRoot 'RelayDiagnosticsTests.cs') (Join-Path $root 'src\RelayDiagnostics.cs')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics unit test compilation failed' }
& (Join-Path $test 'RelayDiagnosticsTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics unit tests failed' }
# The test creates only its own relay processes and ephemeral loopback servers.
& node (Join-Path $PSScriptRoot 'relay-diagnostics.cjs') (Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) 'ChatGPT-DurableRelay.exe') $test
if ($LASTEXITCODE -ne 0) { throw 'Isolated relay diagnostics tests failed' }
