# Interstellar Advanced Architecture

## Product direction

Interstellar evolves from a WPF overlay into a Windows-integrated AI environment while preserving the existing glossy UI, artwork gallery, chat, browser, provider settings, and keyboard shortcuts.

The architecture is split into three trust zones:

1. **Interstellar Desktop (user session)** — WPF presentation, WebView2, AI provider orchestration, screen capture, clipboard access, hotkeys, user-visible automation, and user-session Windows APIs. Runs as the signed-in user and is not elevated by default.
2. **Interstellar Broker (Windows service)** — a small .NET worker service for machine-scoped tasks that genuinely need to outlive the UI. Runs as `NT AUTHORITY\LocalService` with a restricted service SID. It exposes a small authenticated local IPC contract; it is not a generic command runner.
3. **Interstellar Device Bridge (optional KMDF driver)** — a separate, PnP-oriented kernel component for one explicitly supported hardware interface. It is not needed for normal app operation, is never installed by the base app, and is not a general Windows capability provider.

```text
┌─────────────────────────────────────────────────────────────────┐
│                      INTERSTELLAR DESKTOP                        │
│  Existing WPF + WebView2 UI · AI · Browser · Voice · Vision      │
│  User-session automation · Hotkeys · Window/clipboard/screenshot │
└───────────────────────────────┬─────────────────────────────────┘
                                │ local authenticated IPC
                                │ fixed request/response schemas
┌───────────────────────────────▼─────────────────────────────────┐
│                    INTERSTELLAR BROKER SERVICE                   │
│  LocalService + restricted service SID · policy checks · audit   │
│  Notifications · device discovery · narrowly approved operations │
└───────────────────────────────┬─────────────────────────────────┘
                                │ private device interface
                                │ bounded, versioned IOCTLs only
┌───────────────────────────────▼─────────────────────────────────┐
│              OPTIONAL INTERSTELLAR DEVICE BRIDGE                 │
│  KMDF · PnP · one named hardware target · no arbitrary access     │
└─────────────────────────────────────────────────────────────────┘
```

## Keep these features in user mode

The following do not justify kernel code and remain in the existing WPF application or the broker:

- AI inference, prompts, provider API keys, model routing, and conversation memory.
- Browser, chat rendering, voice, screen analysis, and user-visible approvals.
- Global hotkeys, window enumeration/placement, notifications, clipboard operations, and ordinary process launching.
- Scheduled tasks, application startup, user preferences, and update checks.
- File access and automation in the signed-in user's own profile.

Kernel mode is not a shortcut to more capable UI or automation. A driver adds system-wide failure and security impact, so its scope must be tied to a concrete hardware requirement.

## Desktop responsibilities

Keep the current `OverlayAI` project as the presentation layer initially; do not rewrite the WPF interface merely to add system integration. Introduce feature services behind interfaces so the existing code-behind can gradually delegate without visual regressions:

- `IWindowsIntegration`: hotkeys, active-window context, window placement, notifications, startup registration.
- `IAutomationCoordinator`: explicit user-approved multi-step tasks and cancellation.
- `ISystemBrokerClient`: typed IPC client for the broker; unavailable state must be a normal condition.
- `IDeviceBridgeClient`: optional typed client for broker-mediated device capabilities; no direct driver handle in the UI.
- `IAuditFeed`: visible history of operations requested, approved, executed, and denied.

All user-session work executes with the signed-in user's token. The UI must make capture, clipboard use, voice activation, and external actions visible and controllable. Provider credentials remain in the existing user profile and never enter the driver or broker protocol.

## Broker service

Proposed project: `Interstellar.Broker` (`net8.0-windows`, Worker Service / Windows Service host).

- Run as `NT AUTHORITY\LocalService`, not LocalSystem, unless a future reviewed requirement proves a specific privilege is unavoidable.
- Enable a dedicated service SID and use a restricted service configuration.
- Keep the service non-interactive. It never creates UI, launches arbitrary commands, or accepts arbitrary paths/arguments from the client.
- Expose one local named pipe, for example `\\.\pipe\Interstellar.Broker.v1`, with an explicit DACL. Deny network/remote clients; do not rely on the default pipe ACL.
- Verify the connecting process/session identity and authorize each operation separately. Use client impersonation only where an operation must act on behalf of the signed-in user, and revert immediately.
- Validate message size, version, operation name, enum values, path roots, timeouts, and rate limits. Reject unknown fields and unknown operations.
- Maintain an audit record without storing prompt contents, API keys, or sensitive screen contents by default.
- If the service is stopped, the desktop app degrades gracefully. Core chat, browser, and gallery remain usable.

Only add a service operation when it cannot be done safely and reliably in the user process. Prefer user-mode Windows APIs over a service, and prefer service APIs over a driver.

## Optional KMDF driver

Proposed component: `InterstellarDeviceBridge.sys`, with a separate INF/catalog package and a distinct version lifecycle.

**No hardware target has been specified yet. Therefore this design intentionally does not ship a loadable driver or invent a virtual device.** Before source implementation, document the exact vendor/device IDs, supported Windows versions, hardware protocol, required access, expected failure modes, and why a documented user-mode API cannot do the job.

When a genuine hardware requirement is identified:

- Use KMDF and the Plug and Play / power-management model appropriate to the actual device. Prefer a function driver for a supported device; use a filter driver only if the hardware stack requires it and the need is justified.
- Expose a device interface GUID and a tiny, versioned IOCTL contract to the broker only. Do not expose the interface directly to the desktop UI.
- Start with read-only capability/status queries. Add a write operation only for a documented, bounded hardware setting with range validation and a safe failure state.
- Use `METHOD_BUFFERED` where suitable, explicit input/output structure sizes, `FILE_READ_DATA` / `FILE_WRITE_DATA` access bits as appropriate, and strict per-IOCTL validation. Never use `FILE_ANY_ACCESS` for privileged operations.
- Apply an explicit device-interface ACL granting access only to SYSTEM and the dedicated broker service SID. Do not grant `Everyone`, `Users`, or broad `LocalService` access in the production package.
- Use PnP device interfaces and the device stack's security descriptor; avoid creating extra named device objects or symbolic links unless compatibility requires them.
- No arbitrary kernel memory read/write, physical memory mapping, MSR/port access, process-memory access, process/thread callbacks, keyboard/mouse interception, stealth, persistence tricks, security-tool interference, or arbitrary code execution.
- No AI inference, networking, cloud credentials, policy decisions, file-system traversal, or shell execution in kernel mode.
- The driver must be optional, removable, and independently versioned. Its absence or failure must not disable the application.

The first driver milestone should be a hardware-specific, read-only status/capability interface. If no specific hardware need survives review, ship no driver. That is a valid and preferred outcome.

## Installation and lifecycle

1. Install and run the desktop app without elevation. Do not install a driver as a side effect of first launch.
2. Offer the broker as an optional Windows service only when a selected feature needs it. Request administrator approval only during service installation/upgrade/removal.
3. Install the driver only after a supported physical device is detected and the user explicitly opts in. Show publisher, device purpose, driver version, and uninstall path before elevation.
4. Keep driver package signing, service installation, and desktop updates separate. A normal UI update must never silently replace a kernel driver.
5. Uninstall in reverse dependency order: stop desktop integrations, stop/remove broker if selected, then remove the optional driver package. Never delete a driver package that Windows reports as in use.

## Reliability and observability

- Use structured logs with correlation IDs across Desktop → Broker → Driver requests.
- Record operation name, result, duration, and policy decision; avoid payload capture by default.
- Expose service/driver health as status only, not a remote command surface.
- Add explicit cancellation, bounded waits, and timeouts to every cross-process request.
- Make crash and disconnect paths safe: no automatic retry loops for writes, no duplicate side effects, and no UI hang waiting on a driver.
- Keep an emergency switch in Settings that disables broker-backed integration and all driver access without affecting chat/browser.
