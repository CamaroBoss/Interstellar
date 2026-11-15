# Interstellar Device Bridge (optional KMDF component)

This directory reserves a separate boundary for a future hardware-specific kernel driver. It is intentionally not a working or loadable driver project.

There is no hardware target in the current Interstellar source package. Creating a generic control driver now would be unnecessary and would create a privileged attack surface without delivering a real feature. The base desktop app must continue to work without this component.

Before implementation, complete [`../../docs/DRIVER-IMPLEMENTATION-GATE.md`](../../docs/DRIVER-IMPLEMENTATION-GATE.md). The eventual driver should be a separate Visual Studio KMDF Driver project and INF package, built with the WDK version appropriate for the targeted Windows release. The driver should expose one protected PnP device interface to the Broker Service and a tiny set of documented, versioned IOCTLs for the actual hardware. It must not become a generic system-control interface.

No `.sys`, `.inf`, `.cat`, test certificate, signing bypass, or installation script is shipped in this directory.


## Explicit non-goal

This driver will not hide Interstellar's process or windows, modify process-list enumeration, intercept other applications, defeat capture/monitoring tools, or bypass Windows security. Those are not legitimate hardware-driver responsibilities. Window capture privacy is handled by the documented user-mode display-affinity feature instead.
