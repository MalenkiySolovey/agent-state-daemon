# Agent State Daemon

> **Experimental / pre-stable.** This project is under active protocol and storage development. Persistence, RPC methods, and client interfaces may change before a first stable release. It is not production-ready and does not promise API, IPC, migration, or v1.0 compatibility.

Agent State Daemon is reusable, local-first infrastructure for transactional state described by the [Agent State Protocol](https://github.com/MalenkiySolovey/agent-state-protocol). It is not part of Codex Control Center and does not depend on Codex.

## Current daemon scope

The current implementation persists account metadata only. Its local interface uses JSON-RPC 2.0 over bounded UTF-8 NDJSON on a Windows current-user Named Pipe. SQLite is an internal implementation detail; clients use the daemon protocol and never open its database. Account metadata updates use opaque revisions for optimistic concurrency.

Implemented methods:

- `system.ping`
- `system.info`
- `accounts.list`
- `accounts.get`
- `accounts.updateMetadata`
- `accounts.getNickname`
- `accounts.listNicknames`
- `accounts.updateNickname`
- `shadow.accounts.reconcileSource`
- `accounts.nicknameAuthority.get`
- `accounts.nicknameAuthority.beginPreparation`
- `accounts.nicknameAuthority.markVerified`
- `accounts.nicknameAuthority.activate`
- `accounts.nicknameAuthority.beginRollback`
- `accounts.nicknameAuthority.completeRollback`

The host runs in the foreground. Use `--data-dir <path>` to select a development or test data directory and `--pipe-name <name>` to select a pipe name. Without `--data-dir`, the host uses an application-owned local user-data directory. Keep production data on a local filesystem; network filesystems are unsupported.

## Experimental shadow reconciliation

The host also has an experimental `shadow.accounts.reconcile` method for isolated shadow synchronization. It is available only when the daemon is explicitly started with `--shadow-mode`, an explicit `--data-dir`, and the dedicated `codex-switchboard-agent-state-shadow-v1` pipe. The method transactionally replaces the shadow account metadata snapshot and is not a canonical production mutation API. Normal daemon mode does not expose it.

## Not implemented

Secret or credential storage, providers, model inventories, usage, settings, launch profiles, Codex routing/processes, threads, sessions, leases, chat/project sharing, Control Center migration, Unix sockets, HTTP, cloud sync, service installation, and automatic startup are outside the current scope.

No release, installer, service package, or NuGet package is published by this repository at this experimental stage.

## Experimental nickname-authority foundation

The daemon vendors the exact Stage 3E.1 protocol snapshot and implements durable nickname state, source-owned reconciliation, compare-and-set updates, and the daemon-private authority lifecycle. Fresh and existing databases remain at SQLite `user_version=1`; only an explicit `Verified -> DaemonNicknameAuthority` activation transaction writes the v2 compatibility fence. Rollback returns the semantic state to `LegacyLocal` but deliberately keeps the database at v2.

This foundation is disabled by default. The lifecycle operations require `--experimental-nickname-authority`, plus an explicit `--data-dir` and an explicit test pipe or the dedicated `codex-switchboard-agent-state-nickname-authority-v1` pipe. Use only an isolated synthetic data directory for this experimental mode. The flag does not itself confer authority: after a durable transition, persisted SQLite state controls the fences even when the flag is later omitted. The current Codex Control Center remains the production nickname owner; no product migration or cutover is implemented here.
