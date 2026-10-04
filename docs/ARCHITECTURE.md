# Architecture

USB A1644 interface 01 → WinUSB → SYSTEM worker → restricted shared memory → persistent UMDF HID keyboard → Windows.

The worker accepts the known 10-byte keyboard report (ID 1), maps Fn/Control and optionally Option/Command, and emits an 8-byte boot-style keyboard payload. Read timeouts do not release held keys. Disconnect/error disposal sends a neutral report. A fast disconnect/reconnect retries a small bounded number of times when the device is already present; a persistent worker failure requests recovery. Input text and raw reports are not logged.

## Installation ownership

Each installation has a random ID and a separately reserved HIDMaestro controller index (1024–65535). The root hardware ID is root\MagicKeyboardBridge. Registry ownership, device identity, process image, process creation time and SYSTEM session are checked before reuse. The program does not call the upstream global installation/removal APIs. The virtual node persists over worker restarts; removing and recreating it during boot was unreliable in the predecessor.

Files are staged under %ProgramData%\MagicKeyboardBridge with administrator/SYSTEM write access and ordinary-user read/execute. Reparse-point ancestors are rejected. The public ZIP excludes the upstream aggregate assembly. Before elevation, the launcher downloads it directly from the pinned upstream release, verifies both archive and DLL hashes, then adds only that DLL to the copy manifest. Downloads have idle/total time limits; corrupt cache entries are rejected. A source launch reuses the already verified build dependency. Driver tool extraction includes both architecture-specific resources and the architecture-neutral catalog tools. The native user-mode payload is unchanged apart from a local signature; the INF uses this project's hardware ID and service name.

Input/output sections and the input event grant SYSTEM, administrators and LocalService access. Unlike upstream's diagnostic mapping, ordinary users are not given read access to shared keyboard reports. The underlying driver retains upstream behavior for its other objects; a hostile local administrator is outside this boundary.

## Installation state

1. Validate package hashes, protected destination, supported OS and one Microsoft-bound A1644 without filters.
2. Allocate owned identity; prepare a machine-local certificate and driver packages.
3. Create the virtual node and observe an F24 down/up twice across connection teardown. Stop before physical takeover if this fails.
4. Register independent deadline recovery and SYSTEM startup/health tasks.
5. Bind only USB 05AC:0267 interface 01 to WinUSB. Verify SYSTEM report delivery, graceful worker restart and delivery after restart.
6. Start the actual scheduled health task and require a fresh successful SYSTEM result. Remove the deadline guard only then.

Health examines the native CM device tree, not a guessed WMI child hardware-ID pattern. Installer/recovery/uninstall serialize mutations through one lifecycle lock. A running worker is accepted as already installed only when its owned startup/health tasks have enabled boot triggers, run as SYSTEM, and the recovery-armed marker is present. Failed task registration before takeover disables partial tasks and removes the unused deadline guard. Result files carry a run ID so a stale success cannot finish a new launcher. Native helper commands have time limits and retain stdout, stderr and integer exit status.

Restore rebinds the owned physical interface to Microsoft's compatible input.inf. Uninstall restores first, then removes only the owned virtual node, byte-matching OEM packages, owner-matching configuration and installation certificate. Failed recovery retains diagnostic files. No script changes BCD, test signing or Secure Boot.

## Boundaries still requiring validation

Clean-system signing with test signing already off, the new independent device identity, restricted IPC, reboot/replug/sleep and recovery under injected failures all need live validation. Offline tests cannot establish USB timing, driver acceptance or game compatibility. A functional prototype is evidence for the approach, not a substitute for these checks.
