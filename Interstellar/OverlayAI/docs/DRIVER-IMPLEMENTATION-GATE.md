# Interstellar Device Bridge — Implementation Gate

## Current status

**Design only. No `.sys`, INF, CAT, service installer, or test-signing configuration is included.** The project archive currently has a WPF .NET 8 desktop app; no hardware target is identified. A generic driver would add risk without adding a real capability.

## Required hardware brief before coding

Complete every item before creating a driver project:

1. Device manufacturer, product name, hardware IDs, and interface/bus (USB, PCIe, ACPI, HID, etc.).
2. Exact operation required and the source of the device protocol specification.
3. Why a documented Windows API, existing vendor driver, HID/WinUSB interface, or user-mode service cannot satisfy the requirement.
4. Supported Windows editions/builds and x64/ARM64 requirement.
5. Read-only vs. write operations; bounded input/output; safe device state after failure.
6. Signing, distribution, update, recovery, rollback, and uninstall plan.
7. Test hardware and a disposable Windows test environment.

## Driver design constraints

- KMDF, PnP, non-boot-start, no unload hacks.
- One supported device family; no universal arbitrary device access.
- Broker-only device interface protected by an ACL for the dedicated broker service SID and SYSTEM.
- Explicit versioned IOCTLs with typed fixed-layout structures. Prefer read-only queries first.
- No `FILE_ANY_ACCESS` for operations that change state; choose IOCTL access bits matching the operation.
- Do not expose physical memory, kernel virtual memory, MSRs, I/O ports, process handles, thread context, or arbitrary device registers.
- Validate every buffer length and field. Avoid unbounded loops, blocking waits, and allocations based on unchecked user-controlled lengths.
- The driver performs only device I/O and state validation. All product policy, identity, AI, networking, and user consent remain in user mode.

## Development environment

Use Microsoft's currently supported Visual Studio + WDK pairing for the target Windows version. On the date this design was prepared, Microsoft's WDK download guidance identifies WDK 28000.2526 with Visual Studio 2026 as the current default, and WDK 26100.6584 for teams staying on Visual Studio 2022. Reconfirm the supported pairing before starting development because WDK releases change.

Build and test in a disposable VM or dedicated test machine. Use a test certificate only for development, and follow Microsoft's current test-signing procedure. Public distribution on supported 64-bit Windows requires a driver package signed through Microsoft's hardware developer signing process; do not ask end users to disable Secure Boot or code-integrity protections.

## Decision rule

If the hardware brief does not identify a concrete, unavoidable kernel responsibility, do not implement or distribute the driver. Continue with the user-mode Desktop + Broker architecture.

## Microsoft references

- [Download the Windows Driver Kit (WDK)](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk)
- [Controlling Device Access in KMDF Drivers](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/controlling-device-access-in-kmdf-drivers)
- [Signing a Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/signing-a-driver)
- [Kernel-Mode Code Signing Requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-)
- [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [LocalService Account](https://learn.microsoft.com/en-us/windows/win32/services/localservice-account)
