# AG2 Router

A minimal, professional, lightweight local Windows application and service for Antigravity 2 that monitors quota across multiple connected accounts and enables safe, automated routing.

---

## 1. What AG2 Router Is

AG2 Router is designed to solve session exhaustion during heavy Antigravity 2 agent workflows. It provides:

* **Real-time Quota Monitoring:** Tracks per-model quotas and prompt/flow credits across connected accounts.
* **Deterministic Selection:** Evaluates account health and selects the optimal account when the current quota drops below threshold.
* **Workload-Aware Safety Gating:** Prohibits switching whenever Antigravity 2 is actively executing tasks (`BUSY` or running trajectories $> 0$).
* **Minimalist Local Dashboard:** A clean, accessible, zero-dependency web interface served exclusively on loopback (`127.0.0.1`).
* **Zero Runtime Dependencies:** Built strictly on Node.js standard libraries (`node:http`, `node:fs`, `node:crypto`, `node:test`).

---

## 2. Current Implementation Status

> [!IMPORTANT]
> **Stage: Foundation & Setup**  
> This repository contains the complete, validated architectural foundation, domain models, candidate selection engine, safety gate state machine, local loopback server, and minimal dashboard shell. Real credential capture, WinCred session swapping, process termination/respawn, and live Connect-RPC telemetry are staged for upcoming implementation phases.

| Capability | Current Status | Notes |
| :--- | :--- | :--- |
| **Project Architecture & Contracts** | **Operational** | Clean boundary interfaces for AG2, accounts, router, and server |
| **Candidate Selection Engine** | **Operational** | Deterministic ranking, threshold evaluation, tie-breaking |
| **Idle Safety Gate** | **Operational** | State machine enforcing `LOW QUOTA → SWITCH PENDING → WAIT FOR IDLE → SWITCH → VERIFY` |
| **Account Metadata Store** | **Operational** | Non-secret storage (In-memory and atomic local filesystem persistence) |
| **Local HTTP Server & API** | **Operational** | Loopback-only (`127.0.0.1`), security headers, traversal prevention |
| **Dashboard Interface** | **Operational** | Responsive vanilla HTML/CSS/JS dashboard shell with truthful empty states |
| **Automated Test Suite** | **Operational** | 33 comprehensive tests across 6 suites using native `node:test` |
| **Live Connect-RPC Telemetry** | *Staged (Phase 2)* | Interface defined; real RPC calls reserved for next stage |
| **DPAPI Session Vault** | *Staged (Phase 3)* | Interface defined; encrypted session capture reserved for next stage |
| **Cold Account Switching** | *Staged (Phase 4)* | Interface defined; process recycling reserved for next stage |

---

## 3. Architecture & Domain Boundaries

The codebase enforces strict separation of concerns to ensure unofficial Antigravity 2 changes never impact the router domain or dashboard:

```
src/
├── ag2/         # Antigravity 2 adapter boundary (IAG2Adapter)
├── accounts/    # Non-sensitive account metadata & storage (IAccountStore)
├── router/      # Quota assessment, candidate selection, & idle safety gate
├── server/      # Loopback HTTP server & REST API surface
├── ui/          # Vanilla HTML/CSS/JavaScript dashboard
├── config/      # Runtime settings with conservative defaults
└── index.ts     # Main application entrypoint
```

* **AG2 Adapter Boundary:** Unofficial and version-sensitive process detection, Connect-RPC calls, and credential operations remain isolated inside `src/ag2/`.
* **Segregated Quotas:** Prompt and Flow credits are distinct resource pools and are never combined into a single balance.
* **Safety Gate Contract:** Under no circumstances will a switch execute while Antigravity 2 is busy.

---

## 4. Development & Quick Start

### Prerequisites

* Node.js 20.0.0 or higher
* npm 9.0.0 or higher
* Windows 10/11 (target production platform)

### Commands

```bash
# Install development dependencies (TypeScript and Node types)
npm install

# Typecheck source code without emitting files
npm run typecheck

# Build TypeScript and copy UI assets to dist/
npm run build

# Run automated unit and integration tests
npm test

# Start the application locally
npm start

# Development mode (compiles and launches server)
npm run dev
```

Once started, open your browser and navigate to the loopback dashboard:
```
http://127.0.0.1:39250
```

---

## 5. Security Principles

1. **Loopback Only:** AG2 Router binds strictly to `127.0.0.1`. Remote requests outside loopback are blocked with `403 Forbidden`.
2. **Zero Plaintext Secrets:** Passwords, OAuth tokens, and session secrets are never stored in plaintext or placed in source control.
3. **No Unrequested Process Interruption:** Account switching requires confirmed `IDLE` state to avoid interrupting long-running agent trajectories.
4. **Hardened HTTP Surface:** Enforces `nosniff`, `DENY` clickjacking mitigation, `no-store` caching, strict path traversal detection, and tight Content Security Policy (`CSP`).

For full details, see [docs/security.md](docs/security.md).

---

## 6. Current Limitations

* **No Live Credentials Yet:** Accounts registered in the foundation dashboard currently store only non-secret metadata (email, priority, label).
* **Telemetry Displays Empty State:** Because live Connect-RPC telemetry is not connected in this foundation run, the dashboard truthfully indicates `--%` and `Waiting for Antigravity 2`.
* **Switching Does Not Restart Process:** The safety gate and router evaluate selection decisions, but halt before mutating credentials or restarting `language_server.exe`.

---

## 7. Planned Roadmap

* **Phase 2 &bull; Discovery & Telemetry:** Native `language_server.exe` PID/port resolution, HTTPS Connect-RPC client, live UserStatus telemetry parser.
* **Phase 3 &bull; Windows Session Vault:** Windows DPAPI encryption via `ProtectedData` for secure session token storage and WinCred generic credential target (`gemini:antigravity`) management.
* **Phase 4 &bull; Cold Switching Engine:** Safe process termination (`Stop-Process`), WinCred credential swap, binary relaunch, and identity verification.
* **Phase 5 &bull; Autonomous Routing:** Continuous background evaluation and automated switching on low quota with safety gate enforcement.

---

## License

MIT
