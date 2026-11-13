# Generates only a public trust manifest; both private keys stay with their respective custodians.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][guid]$ApproverSubject,
    [Parameter(Mandatory=$true)][guid]$ExecutorSubject,
    [Parameter(Mandatory=$true)][string]$ApproverPublicPem,
    [Parameter(Mandatory=$true)][string]$ExecutorPublicPem,
    [Parameter(Mandatory=$true)][string]$OutputManifest
)
$ErrorActionPreference='Stop'
if ($ApproverSubject -eq $ExecutorSubject -or
    $ApproverSubject -eq [guid]'ab2d17fc-2630-4991-8533-2d83316cc82a' -or
    $ExecutorSubject -eq [guid]'ab2d17fc-2630-4991-8533-2d83316cc82a') {
    throw 'Custodians must have different identity subjects from one another and from fc2-admin.'
}
function SpkiBase64([string]$file) {
    $pem=[IO.File]::ReadAllText((Resolve-Path $file))
    if ($pem -notmatch '(?s)^\s*-----BEGIN PUBLIC KEY-----\s*(.*?)\s*-----END PUBLIC KEY-----\s*$') {
        throw "Expected an RSA SPKI public-key PEM: $file"
    }
    return [Convert]::ToBase64String([Convert]::FromBase64String(($Matches[1] -replace '\s','')))
}
$first=SpkiBase64 $ApproverPublicPem
$second=SpkiBase64 $ExecutorPublicPem
if ($first -eq $second) {throw 'Custodian public keys must be distinct.'}
if ([IO.File]::Exists($OutputManifest)) {throw 'Manifest already exists; refusing to overwrite.'}
$issuer='https://localhost:8443/realms/factoryconnect-dev'
$start=[DateTimeOffset]::UtcNow.AddDays(-1).ToString('o')
$end=[DateTimeOffset]::UtcNow.AddDays(90).ToString('o')
$approverKey=[guid]::NewGuid()
$executorKey=[guid]::NewGuid()
$manifest=[ordered]@{
  installationId='AAE0C57A-BEA6-427F-8046-4E22DDFD63E9'
  allowedAdministratorIssuer=$issuer
  keys=@(
    [ordered]@{ keyId=$approverKey.ToString(); custodianId=([guid]::NewGuid()).ToString(); issuer=$issuer; subject=$ApproverSubject.ToString(); publicKeySpki=$first; validFrom=$start; expiresAt=$end },
    [ordered]@{ keyId=$executorKey.ToString(); custodianId=([guid]::NewGuid()).ToString(); issuer=$issuer; subject=$ExecutorSubject.ToString(); publicKeySpki=$second; validFrom=$start; expiresAt=$end }
  )
}
$json=ConvertTo-Json -InputObject $manifest -Depth 6
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputManifest),$json,[Text.UTF8Encoding]::new($false))
Write-Host "Public manifest: $OutputManifest"
Write-Host "ApproverKeyId: $approverKey"
Write-Host "ExecutorKeyId: $executorKey"
Write-Host 'Retain these IDs for the 10-minute bootstrap prepare/sign/assemble/apply ceremony.'
