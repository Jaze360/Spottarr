# Windows preview verification — 19 October 2026

Baseline: Spottarr upstream `c87766485b69eda722aba74f8c6dd842275539c0`.
Toolchain: local .NET SDK 10.0.401; Windows x64; Inno Setup 6.7.3.

## Verified locally

- Original solution Release build succeeds. NuGet emits inherited NU1902/NU1903 warnings
  for System.Security.Cryptography.Pkcs/Xml 10.0.12; those are not resolved by this change.
- All 200 original tests pass, with zero failures or skips.
- All 28 Windows checks pass: actual upstream option binding, validation failures, encryption,
  private directory/file ACLs, atomic replacement, malformed JSON and ciphertext rejection,
  unknown-setting preservation, launch environment and secret-free arguments, task XML,
  real temporary Task Scheduler registration/removal, job lifetime, four rendered pages and
  all five masked secret inputs.
- Self-contained win-x64 publish succeeds for both the wizard/launcher and the Web server.
- The real published server migrates a fresh temporary SQLite database at the requested path,
  responds to health and capabilities requests, accepts a search with the configured API key,
  and returns a Newznab error for an anonymous search. Captured output contains neither
  the dummy Usenet password nor the dummy Newznab key.
- CSharpier checks for the new C#/project files and the modified SQLite helper pass.
  `git diff --check` passes.
- Inno Setup compiles a per-user installer. A portable ZIP and SHA256 checksum file are produced.

The native smoke test revealed that upstream ignored Database.ConnectionString for SQLite.
The small server change uses it when nonempty and preserves the existing path fallback when empty.
No PostgreSQL, indexing, search or Docker startup algorithm was replaced.

## Not verified / still missing

- Real provider authentication, group retrieval and indexing using a paid/free Usenet account.
- Connection to the user's actual Prowlarr instance and its complete indexer setup/search workflow.
  The wizard implements a connectivity check; creation of the Prowlarr indexer is manual.
- Full interactive install, upgrade, uninstall, Windows sign-out/sign-in and reboot testing.
  Temporary task registration and native binaries were tested; these are not a substitute for that lifecycle test.
- Installer code signing, automatic updates, machine boot service startup, existing-installation
  migration and a LAN listener. Startup is after the current user's sign-in.
- Remediation of the upstream dependency vulnerability warnings.

Only dummy credentials were used. Existing scheduled tasks named Spottarr were not altered.
The installer has not been installed into the user's normal application directory during this work.
Treat the downloadable artifacts as an unsigned preview requiring the remaining checks above.
