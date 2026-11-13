# Local developer quick start (Windows)

This is for the isolated `FactoryConnect_V2_Dev` database only. It does not initialize SQL security, create users, unseal the installation, apply migrations, or activate machines.

1. Copy `scripts/dev/local.settings.example.json` to `scripts/dev/local.settings.json`. Set `siteId` only after a real site exists. Never commit the local settings file or credentials.
2. Start the existing Keycloak Docker Compose stack separately, using its established compose configuration.
3. Run `powershell -File scripts/dev/Start-Local.ps1 -Action Check` to inspect readiness.
4. In separate PowerShell windows run `-Action Simulator`, `-Action Api`, and `-Action Dashboard`. The API prompts for the Keycloak client secret if absent in the current shell.
5. Once the installation has completed the supported signed bootstrap ceremony, sign in using the realm's configured user.

Known identifiers: realm `factoryconnect-dev`; client `factoryconnect-api`; user `fc2-admin` with subject `ab2d17fc-2630-4991-8533-2d83316cc82a`; installation `AAE0C57A-BEA6-427F-8046-4E22DDFD63E9`; simulator device `fc2-local-sim-0001`; machine `49befb26-0ac8-4bdf-87fe-a4ff9e6f3bfd`.

**Current blocker:** security installation is sealed (`IsSealed=1`), epoch 0, and has no principals. The supported signed two-custodian workflow is documented in `docs/implementation/FC2-G2-initial-bootstrap.md`. Do not change `IsSealed` directly or use test fixtures against this persistent database.

Do not store client secrets, custodian private keys, tokens, or production connection strings in source control.
