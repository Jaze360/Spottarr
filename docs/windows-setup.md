# Windows native setup (preview 0.1.0)

This fork adds a Dutch Windows Forms setup wizard and launcher. No Docker is required.
The upstream Web, Services, Configuration and PostgreSQL implementations are unchanged.
SQLite now honors a nonempty Database.ConnectionString; an empty value retains the original
database path behavior. Upstream previously ignored this setting for SQLite, which was caught
by the real server smoke test. The wizard uses an explicit private path.
Baseline: upstream commit `c87766485b69eda722aba74f8c6dd842275539c0` (19 October 2026).
The MIT license and original copyright remain in LICENSE and in the distribution.

## Install and configure

Use 64-bit Windows 10/11 with a supported Windows/.NET combination. The self-contained
package includes .NET 10; installing a separate runtime is unnecessary. This is an unsigned preview.

1. Run the per-user Setup installer, or extract the entire portable ZIP to a permanent folder.
2. Open `Spottarr.Windows.exe`. Do not move the folder after registering startup.
3. Enter the Usenet host, username, password, provider port, TLS and connection limit.
   TLS defaults to true/563. Disabling TLS sends credentials over an unencrypted Usenet connection.
4. Review Spotnet groups, retrieve-after date, retention, batch size and schedules.
   These are **Quartz** cron expressions, not Linux five-field cron. The defaults are copied
   from upstream: import `0 */5 1,3-23 * * ? *`, cleanup `0 0 2 * * ? *`.
5. Choose the HTTP port via `http://127.0.0.1:8383`. Only loopback is supported by this wizard.
   Use a SQLite filename inside `%LOCALAPPDATA%\Spottarr`; quote connection-string paths
   containing semicolons using normal SQLite connection-string syntax. The wizard never moves databases.
6. Save, then start Spottarr. An active server must be stopped and restarted before new settings apply.
7. Optionally register automatic startup after **this Windows user signs in**. This uses
   an interactive, non-elevated task and stores no Windows password. It is not an unattended
   machine boot service. Restart on failure is enabled; the task has no 72-hour time limit.
8. Run the connectivity test. A Prowlarr URL is optional; enter its API key to test its status API.
   That key is cleared after testing and never saved. Remote Prowlarr URLs require HTTPS;
   HTTP is allowed only for loopback and redirects are disabled.
9. In Prowlarr on the **same Windows machine**, add Generic Newznab, use Spottarr's local URL
   and the Newznab key from the wizard's copy button. Upstream Newznab lives at `/newznab/api`.
   Prowlarr may require `/newznab` as the API path for this indexer. Test in Prowlarr to verify
   its chosen API path. Connectivity testing does not create or configure the indexer.
   A Prowlarr running in Docker or on another computer cannot reach this loopback listener.

The copy button intentionally copies only the Newznab API key. Clear the clipboard and its
history after pasting it. Passwords have no reveal or copy button. The wizard doesn't test
Usenet login: provider authentication and actual indexing need a real account and remain
to be verified by the operator.

## Storage and security

`%LOCALAPPDATA%\Spottarr\windows.json` is a versioned JSON envelope with settings and a
Windows DPAPI CurrentUser encrypted credential blob. Usenet username/password, Newznab
and Admin API keys are removed from the public settings before writing. The wizard validates
required fields, numeric bounds, boolean values, host, groups, Quartz cron, database location
and loopback URL before saving and again before launch. Invalid JSON or ciphertext fails closed.
Writes use a new file, flush to disk and atomic replacement; there is no plaintext backup.
Directory ACL inheritance is disabled and only the current user and SYSTEM are granted access.
The SQLite directory is restricted the same way at launch. Choose a fresh dedicated directory;
do not put a junction/symlink into the private data or application directories.

DPAPI protects data at rest, not against software running as the same user, administrators,
process memory inspection or an already compromised session. Moving the JSON to another user
or computer will generally make it unreadable; re-enter credentials instead. Managed strings
and the child environment necessarily contain plaintext while running. They are never placed
on the command line or written to a launcher log. The Usenet log category is disabled for native
launches. Upstream production ASP.NET logging is Warning, suppressing ordinary request URLs
that would contain a Newznab API key. Do not enable verbose HTTP/NNTP diagnostics with secrets.
The installer and portable archive contain only unconfigured upstream defaults.

Unknown settings in a saved envelope survive edits. This version does not import an existing
plaintext appsettings file, Docker environment or an existing scheduled task. Do not manually
put extra secrets in arbitrary JSON properties: only the four documented credential paths
are encrypted. Passwords and keys are masked in the UI and errors do not dump configuration.

The launcher passes decrypted settings via the child process environment to the existing
ASP.NET configuration system. Its working directory is `server/`, so native appsettings and
static web assets resolve correctly. A per-session mutex prevents a second wizard-managed
server; unrelated existing Spottarr processes are not stopped or modified. Windows job objects
terminate the child if the launcher exits or the task is ended. Termination is abrupt; back up
SQLite only while the server is stopped or through a SQLite-aware backup procedure.

To stop: end the `Spottarr.Windows.<user SID>` task in Task Scheduler, or end the launcher
process whose command line contains `--run` in Task Manager. To restart: use Start in the
wizard or Run in Task Scheduler. Remove automatic startup from the wizard before deleting a
portable folder. Installer uninstall removes that user's startup task and preserves the JSON
and database. A pre-existing task named `Spottarr` is left alone; disable it yourself before
using the same port/database to avoid conflicts.

## Build and verification

Use PowerShell 7 and the .NET SDK specified by `global.json` (10.0.401). From the repository root:

```powershell
./windows/Build.ps1 -OutputDirectory C:\build\fresh-output
# Optional installer, with Inno Setup 6 installed:
./windows/Build.ps1 -OutputDirectory C:\build\another-fresh-output -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

The script builds the original solution, runs upstream tests and the Windows-specific checks,
publishes self-contained win-x64 binaries, smoke-tests the real native server with dummy
credentials and a temporary database, then creates the ZIP and optional installer plus SHA256 hashes.
Never publish a configured user directory. No secrets or real provider credentials are needed for tests.
The Windows projects are deliberately outside the original solution so Linux/Docker builds
retain their original target graph. The separate Windows CI workflow covers these projects.

Windows checks cover rejection of invalid input, DPAPI roundtrip, absence of plaintext secrets,
ACL inheritance, atomic replacement, malformed JSON/ciphertext, preservation of unknown settings,
secret-free command arguments, task XML escaping and least privilege, job lifetime, rendering
of all four pages and masked inputs. Native smoke testing covers SQLite migration, health,
capabilities, authenticated search and rejection of anonymous search (capabilities are public
upstream), and checks captured output for dummy secrets. It doesn't authenticate to Usenet
or mutate a real Prowlarr installation.

## Known preview limits

- No signed installer, automatic updates, machine-wide service, unattended boot startup,
  transactional settings rollback, migration of existing installations, or LAN listener.
- Real Usenet retrieval and end-to-end Prowlarr indexer creation/search require user testing.
- Complete interactive installer install/upgrade/uninstall and reboot/logon testing are not
  implied by a successful compiler build; see the delivered build report for what was actually run.
- The upstream dependency graph currently emits NU1902/NU1903 vulnerability warnings for
  System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml 10.0.12. Those inherited
  dependencies need review/updates before treating this preview as production-ready.
