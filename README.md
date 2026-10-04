# MagicKeyboardBridge

[简体中文](README.zh-CN.md)

Use the Lightning Apple Magic Keyboard A1644 on Windows with **Fn as Control** and **Control as a function layer**. The bridge can also exchange Option and Command on this keyboard only.

**Development preview.** A predecessor has delivered physical keyboard input on one Windows 10 x64 computer with test signing off. This portable installer has automated offline tests, but its complete fresh-install, reboot and recovery sequence has not yet been validated on a clean machine. It is not a certified driver release or a claim of compatibility with every computer or game. See [validation](docs/VALIDATION.md).

## Compatibility

| Item | Target |
| --- | --- |
| Keyboard | A1644, Lightning, no Touch ID; USB ID 05AC:0267, interface 01 |
| Connection | Lightning USB cable; Bluetooth is not implemented |
| Windows | Windows 10 build 19041+ x64; Windows 11 x64 or ARM64 |
| Permissions | Windows administrator authorization |
| Boot settings | Test signing must already be OFF; the installer does not change BCD or Secure Boot |
| Other models | Unsupported; installation stops before takeover |

ARM64 binaries can be built, but physical ARM64 operation is unverified. Managed device policies may prohibit locally trusted drivers. No paid remapping application is required.

## Use a built package

1. Extract the complete package to a writable local folder. Keep a mouse available.
2. Connect exactly one supported keyboard by USB.
3. Double-click **Install.cmd**. First use downloads a hash-pinned dependency from HIDMaestro’s official GitHub release; an internet connection is required. Then accept the Windows administrator prompt.
4. Wait for **Installed** or **AlreadyInstalled**. Success closes the launcher. Errors remain visible with an explicit “press any key” message.

No restart is performed automatically. The installer first tests its virtual keyboard twice while ordinary physical typing remains available. Only after this succeeds does it change the A1644 USB interface. A three-minute independent rollback and a background health check cover the takeover. Failure restores the Microsoft keyboard driver when possible; the mapping then stops, while ordinary typing returns.

**Restore.cmd** restores ordinary Microsoft keyboard input. **Uninstall.cmd** restores input first, then removes this installation's virtual device, matching driver packages, tasks and signing certificate. **Status.cmd** prints the current heartbeat and deliberately waits for a key so its window is readable.

If another remapper already swaps Option and Command, run this from PowerShell:

```powershell
.\scripts\Bootstrap.ps1 -Action Install -KeepOptionCommand
```

Otherwise both programs would exchange those keys. Existing PowerToys settings are never edited. The early machine-specific prototype cannot be upgraded by this package; an existing unknown installation is preserved and setup stops.

## Key map

| Physical key or chord | Windows output |
| --- | --- |
| Fn | Left Control |
| Control + Left / Right | Home / End |
| Control + Up / Down | Page Up / Page Down |
| Control + Backspace / Enter | Delete / Insert |
| Control + F1…F12 | F13…F24 |
| Control + P / S / B | Print Screen / Scroll Lock / Pause |
| Fn + Control | Right Control |
| Option / Command, by default | Windows / Alt |
| Eject, where reported | Delete |

Other keys and modifiers pass through. The six-key USB report limit remains. Media brightness and volume translation is not implemented.

## Build and test

Run on Windows:

```powershell
.\tools\Build.ps1 -Runtime win-x64
# Or cross-build the ARM64 package:
.\tools\Build.ps1 -Runtime win-arm64
```

The first build downloads the pinned .NET SDK if necessary and the pinned HIDMaestro v1.10.0 archive for local testing, verifying SHA-512/SHA-256 before use. It runs mapping, process timeout, argument quoting, dependency extraction, PowerShell 5.1 syntax, ownership and isolated installer-failure checks, then creates a self-contained package in **dist/**. Source **Install.cmd** builds first; this requires internet and takes longer than a prebuilt package.

Tests never bind a physical keyboard. Real installation tests require a disposable Windows machine and separately documented physical checks. See [architecture](docs/ARCHITECTURE.md), [validation](docs/VALIDATION.md) and [contributing](CONTRIBUTING.md).

## How it works

Microsoft WinUSB reads the Apple-specific Fn bit; a small SYSTEM process maps the report and sends standard HID reports to a persistent UMDF virtual keyboard based on [HIDMaestro v1.10.0](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.10.0). The virtual device is reused across worker restarts. This package avoids HIDMaestro's global install/remove operations so unrelated virtual controllers are preserved.

The installer creates a unique local code-signing certificate and trusts it in the machine certificate stores for this installation. It uses Windows' existing kernel components and the upstream user-mode driver. This is not WHQL certification. The trust entry and private certificate are removed by uninstall; no signing key is distributed in source or packages. [Microsoft's signing overview](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/windows-driver-signing-tutorial) distinguishes user-mode package signing from kernel-mode driver signing.

Only counters, health and errors are logged; no typed text or raw key reports are saved. IPC permits SYSTEM, administrators and the UMDF LocalService account. No network communication occurs in the running bridge. Games can impose additional requirements; normal-mode keyboard operation alone does not establish game compatibility.

## License and dependencies

Project source: [MIT](LICENSE). HIDMaestro-derived protocol code and driver payload: [upstream license](licenses/HIDMaestro.txt), with [third-party notices](licenses/HIDMaestro-ThirdParty.txt). The .NET runtime retains its [Microsoft terms](licenses/DotNet-LICENSE.txt) and [third-party notices](licenses/DotNet-ThirdPartyNotices.txt); embedded upstream tools retain their own terms. Source control excludes downloaded binaries, machine settings, certificates and logs. The distributable ZIP excludes HIDMaestro.Core.dll and its embedded signing tools; Install.cmd obtains that aggregate directly from its publisher before elevation. Clean-machine validation remains a release prerequisite.
