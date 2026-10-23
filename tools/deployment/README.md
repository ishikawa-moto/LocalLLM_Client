# Client release management

`git pull` updates source. It does not replace a running .NET process or its published DLL.

1. Review and merge the tested source; fast-forward the primary checkout to the same main commit.
2. Run `tools/Build-ReproducibleClient.ps1` with the pinned SDK and a new external output directory. Restore is a separate explicit setup step.
3. Run `tools/deployment/Stage-ClientRelease.ps1 -PublishDirectory <absolute publish directory> -ReleaseDirectory <new absolute release directory>`.
4. Verify the build manifest, private external configuration, consumer paths, idle state, and DB logical schema/rows before activation. Stop intake before checking for queued work. Do not start another instance to test a locked production DB.
5. With deployment authorization, migrate only necessary additive schema changes and update Bridge, Slack Host and both Continue MCP consumers to the same release and external client configuration. Keep model, token, certificate and data paths separate from immutable product files. Record launcher/config hashes, source commit and process path/birth identity in an installation receipt.
6. Start the owned tasks, verify live capabilities and exact DLL identity, and qualify the real authenticated Gateway and public Slack behavior separately from stub tests. A Continue window may need an official Reload Window to release the previous MCP process.
7. Remove obsolete releases, worktrees and temporary outputs only after no launch configuration, active process or required dependency refers to them. Keep the current source, required models/dependencies/authentication and a small final installation/validation receipt. Do not treat a pull or successful process launch as a completed deployment.

Staging never activates code, modifies configuration or a database, or deletes previous releases. It rejects undeclared files, private client settings, path traversal and reparse points. Unknown deployment failures require inspection and forward repair; an older binary must not be automatically restored against a newer schema.
