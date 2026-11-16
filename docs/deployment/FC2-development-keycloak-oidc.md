# FC2 development identity — Keycloak OIDC

This is a **local-only** Keycloak + PostgreSQL environment for Windows developers. It reuses the production-capable OIDC protocol and the existing `OperatorLogin` middleware; it is **not** a production configuration, SQL bootstrap shortcut, or automatic user provisioning mechanism.

## 1. Prerequisites

- Docker Desktop with Linux containers running.
- A local certificate for DNS name `localhost`, with PEM certificate and unencrypted PEM private key in a protected folder **outside this repository**. The certificate's issuing root must be trusted by Windows/.NET and the browser. Do not commit the private key. A trusted local CA such as mkcert can issue these files; use its documented Windows installation and root trust steps.
- Port 8443 free. A separate local development SQL installation remains on its own SQL Server instance.
- Two real, independently held custodian signing keys for FactoryConnect SQL initial bootstrap; these are **not** Keycloak administrator credentials.

## 2. Start identity service

In PowerShell, from repository root, use strong unique secrets. These values remain in your current terminal, not in Git:

```powershell
$env:FC2_KC_CERT_DIR = 'C:\fc2-local\identity-certs'
$env:FC2_KC_DB_PASSWORD = Read-Host 'Keycloak database password'
$env:FC2_KC_ADMIN_USER = 'local-idp-admin'
$env:FC2_KC_ADMIN_PASSWORD = Read-Host 'Keycloak bootstrap admin password'
docker compose -f deploy/dev-identity/compose.yaml up -d
```

The certificate directory must contain `tls.crt` and `tls.key`. Check `docker compose -f deploy/dev-identity/compose.yaml logs identity` and browse `https://localhost:8443/admin/`. Do not proceed through a certificate warning; repair local trust instead.

## 3. Configure a realm and confidential client

In the Keycloak admin console, create realm `factoryconnect-dev` (not the master realm). Create a test human account with a permanent password and a stable username. Record the user's immutable Keycloak **subject ID** from the user details; the username or email is not the SQL external subject.

Create an OpenID Connect confidential client with:
- Client ID: `factoryconnect-api`
- Client authentication: **On** (client secret)
- Standard authorization code flow: **On**
- Direct access grants: **Off**
- Valid redirect URI: `https://localhost:5081/signin-oidc`
- Web origin: `https://localhost:5081`
- Valid post-logout redirect URIs: only the exact local application origin when needed

The API validates OIDC ID tokens using the provider's discovery document and signing keys. Use the **exact issuer** returned in the discovery document, normally `https://localhost:8443/realms/factoryconnect-dev`. Never use a fabricated issuer, localhost HTTP or disabled TLS validation.

## 4. Configure the API (same terminal as dotnet run)

```powershell
$env:Identity__Authority = 'https://localhost:8443/realms/factoryconnect-dev'
$env:Identity__ClientId = 'factoryconnect-api'
$env:Identity__ClientSecret = Read-Host 'OIDC confidential client secret'
# Keep the existing dedicated V2 Evidence connection string and InstallationId.
dotnet run --project backend/FactoryConnect.Api -- --urls 'https://localhost:5081;http://127.0.0.1:5080'
```

Verify `https://localhost:8443/realms/factoryconnect-dev/.well-known/openid-configuration` reports an `issuer` exactly equal to `Identity__Authority`. Check that the API is listening with a trusted HTTPS certificate.

**Important:** Keycloak OIDC authentication is only the first boundary. The V2 installation is currently sealed with epoch 0. Sign-in cannot issue a FactoryConnect SQL human session until the two-custodian bootstrap and subsequent enrollment/permissions have been completed through the existing security authority. The bootstrap manifest's `allowedAdministratorIssuer` must match the exact Keycloak issuer, and the administrator `subject` must match the real account's `sub` claim. The initial bootstrap is irreversible and must not be attempted with guessed values.

## 5. Acceptance and operational boundary

Acceptance stages: (1) Keycloak HTTPS discovery succeeds, (2) OIDC client is configured, (3) real human subject/issuer recorded, (4) independently signed SQL bootstrap performed, (5) enrolled/authorized human can obtain SQL session, (6) browser can read permitted commissioning data. A working login page alone is **PARTIAL**, not a commissioned machine.

The Docker configuration binds Keycloak HTTPS only to 127.0.0.1, persists its own PostgreSQL volume and requires explicit credentials. It must not be reused unchanged for production. Production should deploy the same supported Keycloak major version behind a managed TLS endpoint, with external hardened PostgreSQL, backups, MFA, monitoring, upgrades, independent admin accounts and a production-specific realm/issuer. A production V2 installation pins its own exact issuer and separate custodian trust; changing a pinned issuer is not a casual configuration edit.

Stop without deleting persisted data:

```powershell
docker compose -f deploy/dev-identity/compose.yaml down
```

Never run `down -v` unless explicitly intending to destroy the local identity database.
