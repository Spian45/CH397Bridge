# CH397Bridge

Use a **WCH CH397 / CH396 / CH339 / CH336 USB Ethernet adapter on Windows on ARM** (Snapdragon X, Surface Pro X, and other ARM64 laptops).

Many cheap USB-to-Ethernet adapters and hubs, including some sold by Ugreen, use WCH chips. WCH only publishes Windows drivers for Intel/AMD PCs. On an ARM64 laptop the adapter shows up as a "USB 10/100 LAN" device with no driver, and WCH's installer fails with *"Driver pre install failure"*.

CH397Bridge makes these adapters work **without a custom kernel driver, test-signing mode or turning off Secure Boot / Memory Integrity**.

## Is my adapter supported?

Open **Device Manager**, find the adapter (*USB 10/100 LAN* or an unknown device), and check **Properties → Details → Hardware Ids**. Supported IDs:

| Hardware ID | Chip |
|---|---|
| `USB\VID_1A86&PID_5397` | CH397 |
| `USB\VID_1A86&PID_5396` | CH396 |
| `USB\VID_1A86&PID_5395` | CH339 |
| `USB\VID_1A86&PID_5394` | CH336 |

If your adapter only shows up as a CD drive containing a `WCHUSBNIC` folder, it's one of these. The installer handles that.

Tested on: CH397 (fw 0x53) on Windows 11 ARM64 (Snapdragon X). The other chips use the same protocol in WCH's Linux driver but haven't been tested yet. Reports are welcome.

## Install

1. Download this repository (**Code → Download ZIP**) and extract it.
2. Double-click **`install.cmd`** and click **Yes** on the administrator prompt.
3. Plug in the adapter when asked.

That's it. A new network connection called **Ethernet (CH397)** appears and works like a normal wired connection. It starts automatically every time Windows starts.

You need an internet connection (e.g. Wi-Fi) during installation to download the TAP driver.

## Uninstall

Run **`uninstall.cmd`**, from this folder or from `C:\Program Files\CH397Bridge`.

## How it works

```
 Ethernet cable ⇄ CH397 chip ⇄ USB ⇄ WinUSB (Microsoft driver) ⇄ ch397bridge.exe ⇄ TAP-Windows6 adapter ⇄ Windows networking
```

- **WinUSB** is a generic USB driver that ships with Windows on every architecture, including ARM64. The installer assigns it to the adapter, the same as choosing *WinUsb Device* by hand in Device Manager.
- **ch397bridge.exe** is a small background program. It speaks the CH397's USB protocol (taken from WCH's open-source [Linux driver](https://github.com/WCHSoftGroup/ch397_linux)), sets up the chip and copies Ethernet frames in both directions.
- **TAP-Windows6** is the virtual network adapter driver from OpenVPN. It's signed by Microsoft and has an ARM64 build. Windows uses it like any other network card (DHCP, IPv4, IPv6, DNS).

### What `install.cmd` changes on your PC

| Change | Undone by `uninstall.cmd` |
|---|---|
| Compiles `src\*.cs` with the C# compiler built into Windows and installs to `C:\Program Files\CH397Bridge`. No prebuilt `.exe` is downloaded. | Yes |
| Downloads the official OpenVPN installer, checks its **OpenVPN Inc.** signature, and installs **only** the TAP-Windows6 driver (no VPN software). Skipped if the driver is already installed. | Yes (only if CH397Bridge installed it) |
| Creates a dedicated TAP adapter named *Ethernet (CH397)* with OpenVPN's `tapctl.exe`. Existing OpenVPN adapters are not touched. | Yes |
| Assigns Microsoft's WinUSB driver to the CH397 adapter. | Yes |
| Adds a scheduled task *CH397 Ethernet Bridge* that runs `ch397bridge.exe` as SYSTEM at startup. | Yes |

No certificates are added and no security settings are changed.

## Troubleshooting

- **Log file:** `C:\ProgramData\CH397Bridge\bridge.log`. A healthy start looks like:
  ```
  Opened adapter \\?\usb#vid_1a86&pid_5397#...
  CH397 chip type 0x7A, hw 0x50, fw 0x53, MAC 00:FF:...
  Cable connected, 100 Mbps full duplex
  Bridge running
  ```
- **Adapter shows up as a CD drive:** that's its "driver disk" mode. The bridge ejects the CD automatically, and a few seconds later the adapter switches to network mode. You can also eject it by hand in File Explorer.
- **"Waiting for the CH397 adapter" in the log:** the adapter isn't plugged in, or it isn't using WinUSB. Run `install.cmd` again with the adapter plugged in.
- **Old/wrong IP address after first connect:** Windows may keep an old address for a moment. Unplug and replug the cable, or run `ipconfig /renew "Ethernet (CH397)"`.
- **Windows shows 1 Gbps:** that's the virtual adapter's fixed speed. The real link speed (10/100 Mbps) is in the log.

## Limitations

- These chips are 10/100 Mbps. Expect about 60–90 Mbps in practice.
- The bridge runs in user mode, so it uses a little CPU while traffic flows.
- Unofficial project, not affiliated with or supported by WCH (Nanjing Qinheng Microelectronics), Ugreen or OpenVPN. If WCH releases an official ARM64 driver, prefer that.

## Building by hand

`build.cmd` compiles both programs into `bin\` with the .NET Framework compiler built into Windows. No SDK needed.

## License

GPL-2.0, see [LICENSE](LICENSE). The CH397 protocol handling is based on WCH's GPL-licensed Linux driver [ch397_linux](https://github.com/WCHSoftGroup/ch397_linux).
