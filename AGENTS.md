# Repository guidance

- Read README.md and docs/ARCHITECTURE.md before changing module boundaries. Product code belongs in src, fixtures in tests, reusable scripts in tools, and documentation in docs.
- Keep product version in Directory.Build.props, package versions in Directory.Packages.props, and SDK selection in global.json. Commit NuGet lock files. Do not add developer-specific paths or local feeds to the default restore configuration.
- Preserve Windows 11 x64 portability and WinUI 3 controls. Keep elevated work outside the ordinary UI process and validate account, session, process identity, paths and request types.
- Treat startup names, commands, imported analysis and diagnostics as untrusted data. Never execute arbitrary text from an analysis result.
- Keep credentials, subscriptions, device identifiers, databases, raw logs, signing keys, backups and build caches outside Git. artifacts is private local output, never a bulk upload source.
- Changes to startup controls, services, networking or recovery require relevant isolated tests. Do not use a contributor's live network, remote-control services or installed applications as disposable fixtures.
- Run .\test.ps1 for relevant product changes. Run tools/test/Test-Repository.ps1 and tools/security/Test-Workflows.ps1 for repository/process changes. Distribution changes also require tools/test/Test-Release.ps1 and appropriate Windows Sandbox checks.
- Preserve UTF-8 BOM for installer/Upgrade-Coordinator.ps1 and Windows PowerShell 5.1 guest scripts as specified by .editorconfig. Do not bulk re-encode them.
- Use focused commits describing the actual final change. Preserve existing history; do not invent historical commits, test results, signatures or release provenance.
- Only the tag release workflow publishes official packages. A green build, source review, user acceptance and a real runtime check are distinct evidence.
