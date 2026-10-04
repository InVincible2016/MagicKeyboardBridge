# Contributing

Build with tools/Build.ps1 on Windows. The source uses .NET 10.0.401 and a SHA-256-pinned HIDMaestro v1.10.0 dependency. Run tests/Program.cs through its project and tests/Test-Scripts.ps1 and tests/Test-InstallerFlows.ps1 under Windows PowerShell 5.1. Also run tests/Test-DependencyBootstrap.ps1 to verify hash enforcement and archive handling. The flow tests copy real production scripts into temporary fixtures, then substitute Windows task/driver operations; they never authorize real driver installation.

Keep driver access scoped to the A1644 USB interface. Do not enable test signing, disable Secure Boot, remove arbitrary keyboard filters, call global HIDMaestro uninstall, or delete and recreate a healthy virtual device. Recovery and uninstall must verify ownership and preserve input before cleanup.

Use semantic mapping cases, bounded-process tests and fault-injection tests that establish observable behavior. Hardware changes require the release matrix in docs/VALIDATION.md. Document unsupported models instead of broadening a device match without a verified report format. Keep Windows PowerShell scripts ASCII to avoid legacy encoding differences.

Do not commit downloaded DLLs, build outputs, certificates, private keys, machine settings or logs. An issue may contain a redacted status/error and OS/keyboard model; never attach raw keyboard input. Public release assets require hardware validation; exclude the upstream aggregate DLL, which the launcher downloads from its publisher.
