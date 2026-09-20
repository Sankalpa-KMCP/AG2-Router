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

### 3.2 Planned Credential Vault Architecture (Staged Phase)
In subsequent implementation phases, sensitive session management will adhere to proven Windows DPAPI patterns:
* **Target Credential:** Antigravity 2.0 credentials reside in Windows Credential Manager (`WinCred`) under target `gemini:antigravity` (username: `antigravity`).
* **Encryption Scope:** Multi-account session blobs will be encrypted using Windows Data Protection API (`DPAPI`) with `DataProtectionScope.CurrentUser`.
* **Zero CLI Leaks:** Sensitive payloads must be piped over standard I/O (`stdin`/`stdout`), never passed via process command-line arguments or environment variables.
* **In-Memory Hygiene:** Plaintext credential buffers in memory must be explicitly zeroed after use.
* **Integrity Validation:** Vault files will employ SHA-256 checksums to detect file tampering before attempting DPAPI decryption.

---

## 4. Verification & Audit Trail

* All state transitions in the safety gate are logged with human-readable rationale.
* In the dashboard, activity logs display routing assessments without leaking token data.
