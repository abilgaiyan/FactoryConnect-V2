# Local development policy bootstrap

This tool is restricted to the known `FactoryConnect_V2_Dev` database on `.\\FCINVENTORY`, using Windows integrated authentication. It never changes shift or break planning rows.

From the repository root:

```powershell
dotnet run --project scripts/dev/PolicyBootstrap -- preview
dotnet run --project scripts/dev/PolicyBootstrap -- write
```

Run `preview` first. `write` creates or reuses immutable schedule and break policy revisions from the three validated shifts and their excluded breaks. Re-running the same input reuses the existing revision IDs.

**Not yet complete:** This tool deliberately does not write a timezone revision. The `Asia/Kolkata` IANA zone name is known, but the authoritative TZDB rules bytes, TZDB release, resolver version, and gap/fold policy must be sourced and approved rather than guessed. No configuration package is submitted or activated by this tool. Do not use on the pilot production server.
