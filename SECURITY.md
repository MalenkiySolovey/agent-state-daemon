# Security policy

## Current boundary

This experimental stage stores account metadata, including an explicitly user-selected nickname, only. Nicknames and display labels can be personal data and are visible to authorized local daemon clients. SQLite must never store plaintext credentials, OAuth tokens, API keys, TOTP seeds, raw authentication files, or encrypted credential blobs. Do not add secret-bearing fields to the account database.

The Windows Named Pipe is restricted with the current-user pipe option. This is a local same-user trust boundary: another process running as the same user may be able to act as a client. Do not use an obscure pipe name as an authorization mechanism. Elevated and non-elevated processes are distinct pipe clients on Windows; run the daemon and client with the same ordinary-user elevation context.

RPC frames and arbitrary parameters are not written to logs. Repository fixtures and examples must be synthetic and use reserved `.invalid` endpoints. Never include real tokens, API keys, account exports, databases, logs, or local user paths in issues or pull requests.

The experimental nickname-authority lifecycle must be exercised only with an isolated synthetic data directory. The opt-in flag is disabled by default and requires explicit data and pipe arguments. A completed authority activation is durable and keeps the SQLite v2 downgrade fence even after an explicit semantic rollback.

## Reporting a vulnerability

Please use [GitHub's private vulnerability reporting](https://github.com/MalenkiySolovey/agent-state-daemon/security/advisories/new) for this repository. Do not report exploitable details publicly. Never include real credentials or personal account data in a report.

The project is experimental; report the affected commit, operating system, and a minimal synthetic reproduction when possible.
