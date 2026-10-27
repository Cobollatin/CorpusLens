param(
    [Parameter(Mandatory)][string]$HostIp,
    [Parameter(Mandatory)][string]$SshKeyPath,
    [Parameter(Mandatory)][string]$Domain,
    [string]$SshUser = 'ubuntu',
    [string]$LocalSnapshotPath = 'data/snapshots'
)
$ErrorActionPreference = 'Stop'
$key = (Resolve-Path -LiteralPath $SshKeyPath).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$snapshots = (Resolve-Path -LiteralPath $LocalSnapshotPath).Path
if (-not (Get-ChildItem -LiteralPath $snapshots -Filter 'nwt_*.snapshot.json' -File)) { throw 'No snapshots found.' }
if ($Domain -notmatch '^[a-z0-9][a-z0-9.-]+[a-z0-9]$' -or $HostIp -notmatch '^[0-9a-fA-F:.]+$') { throw 'Invalid domain or IP address.' }
$remote = "$SshUser@$HostIp"
$archive = Join-Path ([IO.Path]::GetTempPath()) ("corpuslens-" + [guid]::NewGuid().ToString() + '.tar')
try {
    git -C $repo archive --format=tar -o $archive HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
    ssh -i $key $remote 'sudo mkdir -p /opt/corpuslens/app /opt/corpuslens/snapshots /opt/corpuslens/wallet && sudo chown -R $USER:$USER /opt/corpuslens'
    if ($LASTEXITCODE -ne 0) { throw 'Remote directory creation failed.' }
    scp -i $key $archive "${remote}:/tmp/corpuslens.tar"
    if ($LASTEXITCODE -ne 0) { throw 'Source copy failed.' }
    foreach ($snapshot in (Get-ChildItem -LiteralPath $snapshots -Filter 'nwt_*.snapshot.json' -File)) {
        scp -i $key $snapshot.FullName "${remote}:/opt/corpuslens/snapshots/"
        if ($LASTEXITCODE -ne 0) { throw 'Snapshot copy failed.' }
    }
    ssh -i $key $remote 'tar -xf /tmp/corpuslens.tar -C /opt/corpuslens/app && rm /tmp/corpuslens.tar'
    if ($LASTEXITCODE -ne 0) { throw 'Remote extraction failed.' }
    ssh -i $key $remote 'test -f /opt/corpuslens/.env && ln -sfn /opt/corpuslens/.env /opt/corpuslens/app/deploy/oci/.env'
    if ($LASTEXITCODE -ne 0) { throw 'External deployment environment is missing.' }
    $run = @'
set -e
cd /opt/corpuslens/app/deploy/oci
test -f .env || { echo 'Create .env with Oracle connection and Vercel origin first.' >&2; exit 1; }
test -f /opt/corpuslens/wallet/tnsnames.ora || { echo 'Install the wallet outside the repository first.' >&2; exit 1; }
export DOMAIN='__DOMAIN__'
docker compose -f docker-compose.oci.yml build
# Fresh schema only; runbook explains existing database upgrades.
docker compose -f docker-compose.oci.yml run --rm worker schema-migrate --ivf
for lang in E S; do
  if test -f "/opt/corpuslens/snapshots/nwt_${lang}.snapshot.json"; then
    docker compose -f docker-compose.oci.yml run --rm worker revision-stage --lang "$lang"
    docker compose -f docker-compose.oci.yml run --rm worker revision-activate --lang "$lang"
  fi
done
docker compose -f docker-compose.oci.yml up -d
'@
    $run = $run.Replace('__DOMAIN__', $Domain)
    $run = $run.Replace("`r`n", "`n")
    $run | ssh -i $key $remote 'bash -s'
    if ($LASTEXITCODE -ne 0) { throw 'Remote build, migration, seed, or startup failed.' }
    Write-Host "Deployment commands completed for api.$Domain."

} finally { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
