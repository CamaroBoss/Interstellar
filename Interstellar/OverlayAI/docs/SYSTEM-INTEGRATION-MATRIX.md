# Windows integration capability matrix

| Capability | Component | Privilege | Driver required? | Current behavior / guardrail |
|---|---|---|---|---|
| Global hotkeys | Desktop | User | No | Ctrl+Shift+S screenshot is global; other movement controls are enabled while expanded |
| Overlay/window placement | Desktop | User | No | Normal, visible WPF window; taskbar and Alt+Tab entry remain present |
| Screen capture | Desktop | User | No | One-shot GDI capture only after Ctrl+Shift+S or the capture button; no live monitoring |
| Clipboard assistance | Desktop | User | No | Copies an image and attempts Ctrl+V into the previously focused app; target must support image paste |
| Browser tabs | Desktop / WebView2 | User | No | Independent tabs can be added and closed |
| Windows VPN | Desktop / RasDial | User | No | Controls profiles already configured in Windows; credentials are not stored by Interstellar |
| AI provider calls | Desktop | User | No | Streamed requests; Fast responses reduces sent context and output length |
| API-key storage | Desktop / DPAPI | User | No | Settings-saved keys are protected for the current Windows user |
| Notification area | Desktop | User | No | Not used; the app is a normal taskbar window and has no tray-resident mode |
| Device-specific hardware access | Separate driver project | Broker + driver ACL | Maybe | Not implemented; only add for a justified device-specific use case |

Interstellar does not hide its process, tamper with Windows process enumeration, or require a kernel driver for screen capture or VPN profile management.
