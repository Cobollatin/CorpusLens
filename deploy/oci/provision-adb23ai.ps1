param(
    [Parameter(Mandatory)][string]$CompartmentId,
    [Parameter(Mandatory)][string]$AdminPassword,
    [Parameter(Mandatory)][string]$WalletPassword,
    [Parameter(Mandatory)][string]$WalletDirectory,
    [string]$DatabaseName = 'CORPUSLENS',
    [string]$DisplayName = 'CorpusLens Always Free'
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command oci -ErrorAction SilentlyContinue)) { throw 'OCI CLI is required.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$wallet = [IO.Path]::GetFullPath($WalletDirectory)
if ($wallet.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'WalletDirectory must be outside the repository.' }
New-Item -ItemType Directory -Force $wallet | Out-Null
$payload = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString() + '.json')
try {
    @{ compartmentId = $CompartmentId; dbName = $DatabaseName; displayName = $DisplayName; adminPassword = $AdminPassword;
       dbWorkload = 'OLTP'; isFreeTier = $true; dbVersion = '23ai'; computeModel = 'ECPU'; computeCount = 2;
       dataStorageSizeInGBs = 20 } | ConvertTo-Json | Set-Content -LiteralPath $payload
    $created = oci db autonomous-database create --profile DOT --region us-ashburn-1 --from-json "file://$payload" --wait-for-state AVAILABLE --output json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Autonomous Database creation failed.' }
    $databaseId = $created.data.id
    if (-not $databaseId) { throw 'OCI did not return a database ID.' }
    $zip = Join-Path $wallet 'Wallet_CorpusLens.zip'
    oci db autonomous-database generate-wallet --profile DOT --region us-ashburn-1 --autonomous-database-id $databaseId --password $WalletPassword --file $zip
    if ($LASTEXITCODE -ne 0) { throw 'Wallet download failed.' }
    Expand-Archive -LiteralPath $zip -DestinationPath $wallet -Force
    Write-Host "Wallet extracted to $wallet. Record the database ID in your private deployment notes."
} finally { Remove-Item -LiteralPath $payload -Force -ErrorAction SilentlyContinue }
