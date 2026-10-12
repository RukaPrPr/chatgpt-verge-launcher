param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$TestDirectory,[Parameter(Mandatory=$true)][string]$NodePath,[Parameter(Mandatory=$true)][string]$CodexPath)
$ErrorActionPreference = 'Stop'
$test = [IO.Path]::GetFullPath($TestDirectory)
[void][IO.Directory]::CreateDirectory($test)
Get-ChildItem -LiteralPath $BuildDirectory -File | Where-Object { $_.Extension -in '.exe','.cjs' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test }
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=fixture.invalid',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('fixture.invalid')
    $request.CertificateExtensions.Add($san.Build())
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(1))
    [IO.File]::WriteAllText((Join-Path $test 'cert.pem'),$certificate.ExportCertificatePem())
    [IO.File]::WriteAllText((Join-Path $test 'key.pem'),$rsa.ExportPkcs8PrivateKeyPem())
} finally { $rsa.Dispose() }
$saved = @{}
$values = @{PROXY_TEST_CERTS=$test; PROXY_TEST_WS=(Join-Path (Split-Path $NodePath -Parent) 'node_modules\playwright-core\lib\utilsBundle.js'); PROXY_TEST_CODEX=$CodexPath}
try {
    foreach ($key in $values.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key,'Process'); [Environment]::SetEnvironmentVariable($key,$values[$key],'Process') }
    & $NodePath (Join-Path $PSScriptRoot 'proxy-transports.cjs') $test
    if ($LASTEXITCODE -ne 0) { throw 'Proxy transport tests failed' }
} finally { foreach ($key in $values.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key],'Process') } }
