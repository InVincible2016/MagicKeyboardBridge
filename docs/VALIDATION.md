# Validation record

Development state as of 2026-10-03. No public release is certified by this file.

| Check | Result / scope |
| --- | --- |
| Predecessor physical Fn/Control mapping | User confirmed on one A1644 over USB, Windows 10 x64 |
| Predecessor normal-mode virtual reconnect | F24 down/up observed twice with test signing off |
| Predecessor SYSTEM worker and watchdog | Fresh running/healthy status observed with test signing off |
| Predecessor extended running session | Failed after a transient status-file replacement error; automatic recovery restored Microsoft keyboard input. A retry fix passed isolated file-sharing tests, but sustained physical operation after that fix is pending. |
| Portable mapper / settings / native helper / payload tests | 565 checks plus 517 predecessor mapping checks passed locally; includes both driver catalogs and heartbeat sharing contention |
| Windows PowerShell 5.1 parsing and ownership tests | 25 checks passed locally; fake tasks and temporary directories only |
| Self-contained x64 and ARM64 package builds | Both cross-builds passed locally; x64 standalone self-test passed |
| Dependency bootstrap with downloads mocked | 17 checks passed, covering cache reuse, corrupt downloads, independent DLL hash and archive path traversal |
| Installer orchestration with Windows operations mocked | 35 scenarios / 119 checks passed under Windows PowerShell 5.1; includes takeover failure, recovery failure, missing startup tasks, uninstall and stale launcher results |
| GitHub clean-checkout build and offline checks | x64 and ARM64 builds passed on Windows Server 2025; x64 standalone self-test passed. [Run](https://github.com/InVincible2016/MagicKeyboardBridge/actions/runs/37171230692), source commit f4916c6. |
| Hosted virtual driver gate | Blocked at preflight because the hosted Windows runner had test signing enabled. No driver creation or input test ran. [Run](https://github.com/InVincible2016/MagicKeyboardBridge/actions/runs/37171282428). |
| Portable package clean install | Pending |
| Portable package physical input and restricted IPC | Pending |
| Portable package reboot, sleep, USB replug | Pending |
| Installer cancellation, crash rollback and uninstall | Live tests pending |
| Windows 11 and ARM64 hardware | Pending |
| Secure Boot enabled on a clean machine | Pending |
| Age of Empires IV or other game | No verified compatibility claim |

## Release gates

Use a disposable Windows installation with test signing already OFF and a mouse/backup input path. Record OS build, architecture, security configuration and commit hash. Never run driver-changing tests on a computer someone is using.

- Build both architectures from a clean checkout; run automated tests under Windows PowerShell 5.1 and the standalone executable without a preinstalled .NET runtime.
- The optional workflow-dispatch virtual_driver_gate runs tools/Test-VirtualDriver.ps1 only on a disposable Windows VM, refuses existing installations, leaves physical keyboard bindings alone, and requires actual F24 observation plus cleanup. It does not count a headless/session-isolation failure as a pass. A hosted Windows Server result does not establish Windows 10/11 physical compatibility.
- Before binding a physical keyboard, create the unique virtual device, verify F24 input twice, validate worker/observer cross-session input, and remove only this installation's device/certificate.
- On supported hardware, check Fn/Ctrl, all modifiers, simultaneous held keys, Caps Lock LEDs, navigation layer, six-key rollover and release order.
- Validate manual restore, uninstall and reinstall; duplicate Install.cmd must preserve a healthy worker. Reject stale results, foreign filters, another virtual controller and untrusted/reparse-point paths.
- Interrupt each installation phase after virtual creation and after physical takeover. Verify the independent deadline and ordinary Microsoft input recovery. Repeat with worker crash, watchdog failure and unplug during recovery.
- Test cold startup/restart, sleep/resume and rapid USB disconnect/reconnect with keys held. Check no keys remain pressed.
- Launch each claimed game with normal security settings and verify mapped controls in actual play. Record that game's build and result.
- Confirm the public ZIP excludes the upstream aggregate DLL/signing tools and includes .NET license/notices; first installation fetches the pinned aggregate directly from its publisher. Never include a private key, local device identity, user path or raw diagnostic log.

The hosted-runner failure does not establish normal-mode driver compatibility or incompatibility. A disposable Windows environment with test signing already OFF is still required; changing a BCD value without rebooting would not satisfy that condition. GitHub's [Windows image template](https://github.com/actions/runner-images/blob/main/images/windows/templates/build.windows-2022.pkr.hcl) also explicitly enables test signing, so switching labels is not a reliable substitute.

A failure in any required gate keeps the release a development preview. A compilation or a mocked test must not be reported as a driver installation test.
