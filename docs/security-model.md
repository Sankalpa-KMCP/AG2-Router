# AG2 Router &bull; Credential & Vault Security Model

## 1. Overview & Threat Model

AG2 Router enables local multi-account management and intelligent quota-based routing for Antigravity 2. Because Antigravity 2 stores authentication material in Windows Credential Manager (`WinCred`), handling session data requires strict architectural boundaries to protect developer credentials against local compromise, accidental mutation, and data leakage.

### Security Invariants
1. **Strictly Read-Only WinCred Access:** AG2 Router only reads `gemini:antigravity` to enroll or refresh accounts. It never calls `CredWriteW`, `CredDeleteW`, or alters live credentials.
2. **Single-Layer Windows DPAPI Protection:** Stored session blobs are individually encrypted with Windows Data Protection API (`DPAPI`) scoped to `DataProtectionScope.CurrentUser`.
3. **Internal Identity Framing:** Every encrypted blob includes internal metadata (`accountId`, `target`, `version`) to fail closed on record key alteration or identity mismatch.
4. **Isolated Storage Location:** The session vault defaults to `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat` (outside the Git repository).
5. **No Double Encryption:** Envelope records store individually encrypted DPAPI payloads; outer file encryption is deliberately avoided to keep corruption detection and atomic updates simple and robust.
6. **Fail-Closed Integrity:** Corrupted or invalid vault files are never overwritten or wiped. Operations halt with `VaultCorruptionError` to preserve data for forensic recovery.
7. **Best-Effort Memory Hygiene:** Plaintext input buffers are wiped with zeros (`buffer.fill(0)`). The system documents this accurately as best effort due to Node.js/V8 runtime string pooling and PowerShell subprocess execution.

---

## 2. Windows Credential Manager Boundary

Antigravity 2 stores its live session token in Windows Credential Manager under:
* **Target:** `gemini:antigravity`
* **Type:** `1` (`GENERIC`)
* **UserName:** `antigravity`
* **Payload:** UTF-8 encoded JSON object containing OAuth tokens and authentication metadata.

### Read-Only P/Invoke Surface
To guarantee safety, `AG2WinCredReader` (in `src/ag2/wincred.ts`) implements P/Invoke definitions exclusively for:
* `CredReadW`
* `CredFree`

It contains **no definitions, imports, or methods** for `CredWriteW` or `CredDeleteW`.

### Streaming Standard I/O
The target name is streamed via standard input to PowerShell, and the base64-encoded credential blob is returned via standard output. Credential bytes never appear on process command lines, avoiding exposure in process listings (e.g. Task Manager, Process Explorer, or Event ID 4688).

---

## 3. DPAPI Session Vault Architecture

### Single-Layer Envelope Structure (`sessions.dat`)
```json
{
  "magic": "AG2_ROUTER_SESSION_VAULT",
  "schemaVersion": 1,
  "updatedAt": "2026-09-20T07:15:00.000Z",
  "records": {
    "acc_1a2b3c4d": {
      "accountId": "acc_1a2b3c4d",
      "target": "gemini:antigravity",
      "encryptedPayloadBase64": "AQAAANCMnd8BFdERjHoAwE...",
      "createdAt": "2026-09-20T07:00:00.000Z",
      "updatedAt": "2026-09-20T07:15:00.000Z"
    }
  }
}
```

### Internal Identity Framing
Before DPAPI encryption, the plaintext payload is structured with strict identity metadata:
```json
{
  "version": 1,
  "accountId": "acc_1a2b3c4d",
  "target": "gemini:antigravity",
  "credentialBlobBase64": "eyidb2tlbiI6IC...=",
  "enrolledAt": "2026-09-20T07:15:00.000Z"
}
```

When decrypting:
1. DPAPI decrypts the payload using `DataProtectionScope.CurrentUser`.
2. The payload is parsed and verified:
   - `version === 1`
   - `payload.accountId === record.accountId`
   - `payload.target === record.target`
3. If any field does not match, a `VaultCorruptionError` is thrown immediately. This prevents record swapping or tampering in the envelope.

---

## 4. Atomic Persistence & Corruption Safety

### Atomic Write Pattern
Vault updates are committed using an atomic two-phase write:
1. Serialize the updated `VaultFileEnvelope`.
2. Write to a temporary file: `sessions.dat.<timestamp>.<random>.tmp` with restricted file permissions (`0o600`).
3. Atomically rename the temporary file to `sessions.dat` (`fs.renameSync`).

If writing fails, the `.tmp` file is deleted and the existing `sessions.dat` remains untouched.

### Non-Destructive Fail-Closed Policy
If `sessions.dat` exists but contains invalid JSON, an unrecognized magic identifier, or corrupted records:
* The vault **never** overwrites the corrupted file with an empty template.
* It throws `VaultCorruptionError` immediately.
* Both reads and writes are rejected until the corrupted file is manually recovered or replaced by the user.

---

## 5. Memory Hygiene & Known Limitations

To minimize the exposure window of decrypted secrets in Node.js memory:
* Plaintext buffers passed into `WindowsDpapiProvider.encrypt` are wiped with zeros (`plaintext.fill(0)`) in a `finally` block.
* Decrypted buffers in `SessionVault.getSession` are wiped after decoding base64 tokens.

### Honest Memory Erasure Disclaimer
Developers should understand that in garbage-collected runtimes like Node.js (V8) and PowerShell (.NET runtime):
* Converting buffers to strings or JSON objects creates immutable string primitives on the V8 heap.
* PowerShell memory allocations are governed by the .NET Garbage Collector and cannot be zeroed from user space.
* Therefore, buffer zeroing represents **best-effort defense-in-depth**, not mathematically guaranteed cryptographic erasure.

---

## 6. Secret Redaction & Log Sanitization

`src/ag2/security.ts` provides comprehensive scrubbing for all text, error objects, and command lines before emission:
* Sanitizes `--csrf_token`, `--host_bridge_token`, `--token`, and `--password` CLI flags.
* Sanitizes `x-codeium-csrf-token` and `authorization: Bearer` headers.
* Sanitizes generic `token=...`, `secret: ...`, `key=...` patterns in error messages.
* Exposes only truncated/masked token representations (`fb54...16bb`).
