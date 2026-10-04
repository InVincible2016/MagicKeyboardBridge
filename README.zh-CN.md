# MagicKeyboardBridge

[English](README.md)

让 Lightning 接口的 Apple Magic Keyboard A1644 在 Windows 上使用 **Fn → Control、Control → 功能层**，并可将 Option 与 Command 对调。免费、开源。

**当前是开发预览版。** 早期原型已经在一台 Windows 10 x64 电脑上、关闭测试模式的情况下实际工作。通用安装包通过了离线自动测试，但尚未完成干净电脑上的完整安装、重启、恢复验证。不能据此保证所有电脑或所有游戏兼容。[查看验证记录](docs/VALIDATION.md)。

## 适用范围

- 键盘：A1644，没有 Touch ID，Lightning 接口；USB 标识 05AC:0267，接口 01。
- 连接：必须插 Lightning USB 线，暂不支持蓝牙。
- 系统：Windows 10 19041 及以上 x64；Windows 11 x64 / ARM64。ARM64 实机尚未验证。
- 需要管理员授权。测试模式必须已经关闭；安装器不修改启动设置或 Secure Boot。

其他键盘型号会在接管前被拒绝。受组织管理的电脑可能不允许安装本地信任的驱动。

## 使用打包版本

1. 完整解压到可写的本地文件夹，接好键盘，并保持鼠标可用。
2. 双击 **Install.cmd**。首次运行会从 HIDMaestro 官方 GitHub 发布页下载并校验固定版本依赖，需要联网；随后接受 Windows 管理员提示。
3. 等待 **Installed** 或 **AlreadyInstalled**。成功后窗口退出；失败时会显示原因，并提示按键关闭。

安装前先在保留普通键盘输入的情况下测试虚拟键盘及重连。测试通过后才接管 Apple 键盘，并设置独立的三分钟恢复任务、后台健康检查。出现问题时尽可能恢复微软键盘驱动，让普通输入恢复，对调暂时停止。不会自动重启。

- **Restore.cmd**：恢复微软驱动和普通输入。
- **Uninstall.cmd**：先恢复输入，再卸载本项目拥有的设备、驱动包、任务和签名证书。
- **Status.cmd**：显示状态后等待按键，便于阅读；这是有意保留窗口。

如果 PowerToys 等工具已经负责 Option / Command 对调，请用 PowerShell 运行：

```powershell
.\scripts\Bootstrap.ps1 -Action Install -KeepOptionCommand
```

否则会发生两次对调。程序不会修改 PowerToys 配置，也不会直接升级早期专为某台电脑编写的原型。

## 键位

| 实体键 | Windows 输出 |
| --- | --- |
| Fn | 左 Control |
| Control + 左 / 右方向键 | Home / End |
| Control + 上 / 下方向键 | Page Up / Page Down |
| Control + Backspace / Enter | Delete / Insert |
| Control + F1…F12 | F13…F24 |
| Control + P / S / B | Print Screen / Scroll Lock / Pause |
| Fn + Control | 右 Control |
| Option / Command（默认） | Windows / Alt |

普通键保留，仍受键盘六键报告限制。暂未实现亮度、音量等媒体键映射。

## 从源码构建

```powershell
.\tools\Build.ps1 -Runtime win-x64
# ARM64：
.\tools\Build.ps1 -Runtime win-arm64
```

第一次构建会联网下载并校验固定版本的 .NET SDK 和 HIDMaestro；输出目录为 **dist/**。打包后自带运行时。源码目录中的 Install.cmd 会先构建，耗时取决于下载速度。自动测试不会切换实体键盘驱动。

运行中的桥接程序不联网、不记录输入文本或原始按键报告，只保存状态和计数。安装会为该次安装创建并信任一个本机签名证书，卸载时清理；不会发布私钥。项目使用 HIDMaestro 的用户模式驱动，不代表已获得 WHQL 认证。游戏是否兼容仍需单独实测。

项目代码采用 [MIT 许可证](LICENSE)，依赖保留各自许可。公开 ZIP 不包含 HIDMaestro.Core.dll 及其内嵌的签名工具，而是在安装时从上游官方来源获取；正式发布前仍需完成干净系统验证。
