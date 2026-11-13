# Local developer SQL security bootstrap — two-custodian workflow

**Scope:** only the isolated `FactoryConnect_V2_Dev` installation `AAE0C57A-BEA6-427F-8046-4E22DDFD63E9`. This is initial bootstrap, not ordinary daily startup. Never manually flip `security.InstallationState.IsSealed`.

The administrator binds to Keycloak issuer `https://localhost:8443/realms/factoryconnect-dev` and subject `ab2d17fc-2630-4991-8533-2d83316cc82a`. The subject is not the internal SQL principal GUID (generate that separately). Two **actual, independent custodians**, neither of whom is the administrator, must control their own signing keys. Do not fabricate custodian identities or use two keys controlled by one person to claim independent authorization.

## One-time key generation — each custodian independently

Use a trusted machine and a location *outside the Git repository*. Each custodian executes this with a unique path and keeps their encrypted private PEM and passphrase private. `keygen` prompts twice for a passphrase (at least 12 characters) and never overwrites existing files.

```powershell
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- keygen 'C:\\CustodianSecure\\custodian-private.pem' 'C:\\CustodianSecure\\custodian-public.pem'
```

The custodian supplies only their `custodian-public.pem` and Keycloak user-subject GUID to the administrator. The actual local path will differ per custodian. Keep backup custody and file-system ACLs appropriate to your environment. Never commit either PEM. **An encrypted private PEM remains sensitive.**

## Prepare the public manifest — once

Once both custodians have separate Keycloak user identities, run:

```powershell
powershell -File scripts/dev/Prepare-BootstrapManifest.ps1 `
  -ApproverSubject '<custodian-1-user-id>' -ExecutorSubject '<custodian-2-user-id>' `
  -ApproverPublicPem '<first-public.pem>' -ExecutorPublicPem '<second-public.pem>' `
  -OutputManifest '<outside-repository>\\manifest.json'
```

Record the printed key IDs. Keep `manifest.json` unchanged between prepare, assemble, and apply. If a bootstrap attempt expires, create a new payload/signatures against the *same* manifest, not a new trust record.

## Final timed ceremony — 10 minutes

Start only once both custodians can sign promptly. Work from the repository root. Use an output directory *outside the repo* and fresh filenames for each attempt.

```powershell
$principal=[guid]::NewGuid()
$subject='ab2d17fc-2630-4991-8533-2d83316cc82a'
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- prepare '<manifest.json>' $principal $subject '<approver-key-id>' '<executor-key-id>' 'Initialize isolated V2 dev installation' '<payload.json>' '<canonical.bin>'
# Each custodian separately verifies the payload and signs the same canonical.bin with their private PEM:
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- sign '<private.pem>' '<canonical.bin>' '<approver.sig>'
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- sign '<other-private.pem>' '<canonical.bin>' '<executor.sig>'
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- assemble '<manifest.json>' '<payload.json>' '<approver.sig>' '<executor.sig>' '<signed-request.json>'
$env:FC2_SQL_CONNECTION='Server=.\\FCINVENTORY;Database=FactoryConnect_V2_Dev;Integrated Security=True;TrustServerCertificate=True;'
# Confirm the exact SQL server name using SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')); it may differ from the client alias.
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- FC2_SQL_CONNECTION FactoryConnect_V2_Dev '<exact-sql-server-name>' AAE0C57A-BEA6-427F-8046-4E22DDFD63E9 '<manifest.json>' '<signed-request.json>'
```

The keygen/sign commands require an interactive console for passphrases. They never write passwords to stdout. The SQL command returns a public receipt only if it succeeds. After a successful bootstrap verify `security.InstallationState.IsSealed=0`, `AuthorityEpoch>0`, and a human principal exists. Initial admin grants deliberately distinguish delegation from `UseAllowed`; signing in is **not** proof of all commissioning permissions. Continue with the supported role/grant workflow.

No test fixtures, shortcuts, production credentials, or SQL authority mutations are part of this helper.
