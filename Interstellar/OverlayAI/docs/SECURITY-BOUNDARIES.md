# Interstellar Security Boundaries and Threat Model

## Assets to protect

- Windows kernel integrity and system availability.
- User files, clipboard, screen contents, credentials, and personal data.
- AI provider API keys and local model configuration.
- Broker/driver interfaces and update packages.
- User control over capture, voice, automation, and machine-scoped features.

## Trust boundaries

| Boundary | Trust level | Required control |
|---|---|---|
| User ↔ Desktop | Interactive user | Visible permissions, settings, capture indicator, cancel/pause controls |
| Desktop ↔ Broker | Local IPC | Explicit ACL, client identity, typed allowlist, bounded payloads, audit |
| Broker ↔ Driver | Kernel boundary | Device-interface ACL, narrow IOCTLs, validation, no raw passthrough |
| Installer ↔ Windows | Elevated setup | UAC consent, signed packages, rollback/uninstall, least-privilege service config |
| Desktop ↔ AI provider | External service | TLS, user-controlled provider/key, secret redaction, no driver access |

## Abuse cases and mitigations

- **Untrusted local process connects to the broker:** reject clients outside the intended user/session policy; explicit pipe DACL; do not rely on obscurity of pipe name.
- **Compromised UI tries to turn broker into an elevation oracle:** operation allowlist, no arbitrary command or path execution, per-operation authorization, no generic privileged RPC.
- **Malicious or malformed IOCTL request:** fixed IOCTL numbers and structures, size/version checks, integer-overflow checks, buffer probing through WDF APIs, access-bit checks, bounded work, safe completion status.
- **Driver bug crashes or hangs Windows:** small KMDF codebase, passive-level work where practical, Driver Verifier and static analysis in isolated test VMs, no boot-start driver, no critical-path dependency.
- **AI prompt injection asks for system changes:** model output is untrusted data. High-impact operations require typed policy checks and explicit user approval; the model cannot directly call the broker or driver.
- **Supply-chain tampering:** signed release artifacts, reproducible version metadata, verify driver/catalog signatures, separate driver update channel, reject downgrade or unexpected publisher.
- **Sensitive data leaks into logs:** log metadata and result codes only by default; user opt-in for diagnostic payload capture; redact tokens and personal paths.

## Non-goals / prohibited design patterns

Interstellar is not an endpoint security product, rootkit, anti-cheat, kernel debugger, process-memory editor, keylogger, stealth overlay, security-tool bypass, or generic machine-control driver. No hiding processes/files, disabling security features, disabling code-integrity enforcement, changing boot policy, or bypassing driver signing.

## Release gates

- Threat model and interface review approved before driver implementation.
- Static analysis and code review pass.
- Driver Verifier test pass on supported test hardware/VMs.
- PnP, sleep/resume, surprise-removal, upgrade, rollback, and uninstall tests pass.
- Driver install requires explicit elevation and a trusted signature.
- Base desktop app is verified to run with no service and no driver installed.


## Window capture privacy

The desktop may set `WDA_EXCLUDEFROMCAPTURE` on its own top-level window as a best-effort user privacy feature. This is not process concealment, a security boundary, DRM, or a kernel feature. Interstellar must not hide or falsify process/window enumeration, interfere with endpoint-security products, hook DWM/graphics drivers, or bypass capture consent. See [`PRIVACY-SHIELD.md`](PRIVACY-SHIELD.md).
