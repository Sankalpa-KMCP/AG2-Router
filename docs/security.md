# AG2 Router &bull; Security Architecture & Principles

## 1. Core Security Principles

Security and session safety are foundational requirements of AG2 Router:

1. **Loopback Isolation:** The application process binds exclusively to `127.0.0.1`. It never listens on external network interfaces (`0.0.0.0`), preventing access from other machines on the local network.
2. **Zero Plaintext Secrets:** No OAuth tokens, refresh tokens, passwords, or authentication secrets are ever stored in plaintext files, logs, or version control.
3. **Fail-Closed Design:** In the event of ambiguous state, corrupted files, or unknown process activity, the system halts operations rather than making risky assumptions.
4. **Non-Destructive Idle Protection:** Account switching is strictly blocked whenever Antigravity 2 is actively processing user tasks (`BUSY` or running trajectories $> 0$).

---

## 2. Network & Server Hardening

### 2.1 Loopback-Only Binding
* Node's `http.Server.listen` binds specifically to `127.0.0.1`.
* Every incoming connection is validated against `req.socket.remoteAddress`. Any client originating outside loopback (`127.0.0.1`, `::1`, `::ffff:127.0.0.1`) is immediately rejected with `403 Forbidden`.

### 2.2 HTTP Security Headers
All responses emitted by the server enforce security headers:
* `X-Content-Type-Options: nosniff` &mdash; Prevents MIME-type sniffing.
* `X-Frame-Options: DENY` &mdash; Mitigates clickjacking attacks.
* `Cache-Control: no-store, max-age=0` &mdash; Prevents caching of telemetry, accounts, or status payloads.
* `Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'` &mdash; Restricts resource loading exclusively to local origins.

### 2.3 Path Traversal Prevention
Static asset handling enforces multi-layer directory traversal protection:
1. Rejects any raw URL containing `..`, `%2e%2e`, or backslash characters with `403 Forbidden`.
2. Decodes URI components and verifies that the canonical target path strictly starts with the configured `uiDir`.
3. Disallows directory listing.

---

## 3. Account Storage & Credential Model

### 3.1 Metadata Storage (Foundation Layer)
* File: `data/accounts.json`
* Contents: Strictly non-sensitive metadata (email address, priority rating, reserve flag, validation status).
* Git Hygiene: The `data/` directory is gitignored to ensure local configuration and metadata are never pushed to version control.

### 3.2 Implemented Credential Vault Architecture
The multi-account session vault adheres to proven Windows DPAPI patterns:
* **Target Credential:** Antigravity 2 credentials reside in Windows Credential Manager (`WinCred`) under target `gemini:antigravity` (username: `antigravity`). Access is strictly read-only via `AG2WinCredReader`.
* **Single-Layer DPAPI Encryption:** Multi-account session blobs are individually encrypted using Windows Data Protection API (`DPAPI`) with `DataProtectionScope.CurrentUser`.
* **Zero CLI Leaks:** Sensitive payloads are piped over standard I/O (`stdin`/`stdout`), never passed via process command-line arguments or environment variables.
* **Internal Identity Framing:** Payloads embed accountId and target framing before encryption, failing closed upon record key alteration or identity mismatch.
* **In-Memory Hygiene:** Plaintext credential buffers in memory are wiped with zeros (`buffer.fill(0)`) on a best-effort basis.
* **Fail-Closed Persistence:** Corrupted vault files are never overwritten or wiped. Operations fail closed with `VaultCorruptionError` to preserve data for recovery.
* For full architectural details, see [security-model.md](security-model.md).

---

## 4. Verification & Audit Trail

* All state transitions in the safety gate are logged with human-readable rationale.
* In the dashboard, activity logs display routing assessments without leaking token data.
