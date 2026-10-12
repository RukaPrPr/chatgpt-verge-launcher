param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$TestDirectory,
    [Parameter(Mandatory=$true)][string]$PackageExecutable
)
$ErrorActionPreference = 'Stop'
$test = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $test) { throw 'Use a new isolated test directory.' }
[void][IO.Directory]::CreateDirectory($test)
foreach ($name in @('ChatGPT-Verge-ControlProxy.exe','ChatGPT-Verge-NodeProxy.exe','ChatGPT-Verge-CodexProxy.exe','proxy-preload.cjs')) {
    Copy-Item -LiteralPath (Join-Path $BuildDirectory $name) -Destination $test
}
$resources = Join-Path (Split-Path $PackageExecutable -Parent) 'resources'
$nodeDirectory = Join-Path $resources 'cua_node\bin'
$runtime = [ordered]@{
    PackageExecutable = [IO.Path]::GetFullPath($PackageExecutable)
    Node = Join-Path $nodeDirectory 'node.exe'
    NodeRepl = Join-Path $nodeDirectory 'node_repl.exe'
    Codex = Join-Path $resources 'codex.exe'
}
foreach ($value in $runtime.Values) {
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "Missing runtime: $value" }
}
[IO.File]::WriteAllText((Join-Path $test 'proxy-runtime.json'), ($runtime | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $test 'control-proxy.port'), '17896')
# Generic MCP only: no CUA import, browser interaction, app restart or network call.
& $runtime.Node (Join-Path $PSScriptRoot 'proxy-stdio.cjs') $test
if ($LASTEXITCODE -ne 0) { throw 'Proxy stdio/real Node REPL tests failed' }
