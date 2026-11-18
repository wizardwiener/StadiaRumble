# Stadia Rumble (`v1.0.15`)

[![Version](https://img.shields.io/badge/version-v1.0.15-FF5238?style=flat-square)](https://github.com/wizardwiener/StadiaRumble/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4?style=flat-square)](https://github.com/wizardwiener/StadiaRumble)
[![Transport](https://img.shields.io/badge/transport-Bluetooth%20LE%20%7C%20USB-0082FC?style=flat-square)](https://github.com/wizardwiener/StadiaRumble)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-2EA44F?style=flat-square)](./LICENSE)

> **Language:** English | [Українська](./README.uk.md) | [Русский](./README.ru.md)

Windows 10/11 x64 **UMDF2 HID minidriver** and companion control studio (**Stadia Rumble Studio**) for the **Google Stadia Controller** over **Bluetooth LE** and **USB**. It exposes the controller to games as a standard **Xbox Wireless Controller (XInput)** with full dual-motor vibration support, persistent driver-level profiles (`0xE2`), autonomous driver macros (`0xE3`), and real-time BLE/rumble telemetry (`0xE1`).

*This is an independent open-source project not affiliated with Google or Microsoft. The driver core (`driver/`) is derived from [`aisk/WinStadia`](https://github.com/aisk/WinStadia) under GNU **GPL-3.0** — see [`LICENSE`](./LICENSE), [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md), and [`UPSTREAM_REVISION.txt`](./UPSTREAM_REVISION.txt).*

---

## Measured Stability & Benchmarks

Verified on real hardware running **Windows 11 / 10 Pro 26H2 (Build 26300.9974)** as well as **Builds 26200 & 26220** over Bluetooth LE:

| Metric | Measured Result |
| :--- | :--- |
| **BLE Input Connection Interval** | `7.5 ms` (~133 Hz quantized steps) |
| **BLE Rumble Write-with-Response ACK** | `16 ms` (`WriteLatencyMs`) |
| **Sustained Rumble Stress Test** | `1048 / 1048` successful writes (`0` failures) |
| **Driver / GATT Errors** | `0x00000000` (`DriverError` & `RumbleError`) |
| **Battery Telemetry** | Live percentage via Windows BLE GATT Battery Service (`0x180F`) + `0xE1` |

---

## Features

### 1. UMDF2 HID Minidriver (`driver/`)
- **USB & Bluetooth LE Transport:** Attaches seamlessly as an Xbox Wireless Controller (`VID_045E / PID_02FD`) via `xinputhid` without altering system files or requiring Test Signing Mode.
- **Dedicated Coalesced Rumble Thread:** Separates `IOCTL_HID_READ_REPORT` input queues from BLE GATT rumble writes (`bthserv`), preventing input stalls and vibration lockups. Immediate `(0, 0)` motor-stop priority with bounded exponential backoff.
- **HID Feature Report `0xE1` (Telemetry):** Exposes live transport type, motor state, write/failure counts, last BLE ACK latency (`ms`), driver/rumble `NTSTATUS`, active Turbo status, and raw Stadia input report (including `Assistant` and `Capture` buttons).
- **HID Feature Report `0xE2` (Runtime Profile & Registry Persistence):**
  - Left (`Strong`) and Right (`Weak`) motor gain scaling (`0–100%`)
  - Stick deadzone (`0–25%`), Trigger deadzone (`0–20%`), Hair-Trigger ceiling (`50–100%`)
  - Swap sticks & invert Left/Right Y-axis flags
  - BLE pacing (`IntervalMs`, `PostAckGapMs`, `MinDelta`) tuned to prevent Bluetooth adapter congestion
  - **Hardware Turbo (`Assistant + Button`):** In-driver `WdfTimer` rapid-fire (`5–25 Hz`, **Toggle** or **Hold** mode) for `A/B/X/Y/L1/R1/L2/R2/LS/RS` persisted via `0xE2`
- **HID Feature Report `0xE3` + Native Keyboard (`0x05`) & Consumer (`0x06`) Collections:**
  - `winstadia.dll` exposes native Windows HID Keyboard (`UsagePage 0x01 / Usage 0x06`) and Consumer Control (`UsagePage 0x0C / Usage 0x01`) Top-Level Collections alongside the Xbox Gamepad (`Col01`).
  - `Assistant`, `Capture`, `Assistant + D-Pad` (Volume +/– and Prev/Next Track with full XInput D-Pad & diagonal suppression), and Custom Hotkeys execute natively inside the driver and persist in the PnP device registry (`StadiaMacroSettings`).
  - Zero-delay priority macro emission (`KeyboardChanged`/`ConsumerChanged` dispatched before Gamepad/Guide reports with rapid double-tap recovery, `30 ms` pulse window, clean modifier flush on `D0Exit`) and lossless `HidUsageToVk` custom hotkey synchronization.

### 2. Stadia Rumble Studio (`app/` + `app/web/`)
- **1:1 Web Studio Desktop Interface:** Launching `StadiaRumble.bat` (or `StadiaRumble.exe`) opens the complete modern Stadia Rumble Studio interface in a dedicated standalone Chromium app window powered by a fast local `127.0.0.1` native driver bridge (XInput + `0xE1`/`0xE2`/`0xE3` + Windows BLE Battery + Event Log dumps) — with automatic fallback or `--classic` CLI flag for the lightweight WinForms UI.
- **6 Studio Tabs:** *Gamepad* (interactive Stadia controller monitor & slot `#1–#4` selector), *BLE Monitor* (real-time latency, RSSI, and write/failure charts), *Power* (`0xE1` + Windows BLE Battery history), *Macros & Turbo* (autonomous `0xE3` driver macros, intuitive **Custom Hotkey** keyboard capture with `Esc` cancel + presets, and `0xE2` Hardware Turbo `5–25 Hz`), *WebUSB / `0xE2` Configurator* (all gains, deadzones, Hair-Trigger, stick swap/invert, and BLE pacing + `.bin`/`.reg` export), and *Dump Analyzer* (live Windows WUDF/bthserv scan & `.txt` log analyzer).
- **Zero-Footprint Standalone Mode:** Closing `[X]` terminates `StadiaRumble.exe` cleanly with zero background processes because both Macros (`0xE3`) and Turbo (`0xE2`) run autonomously inside `winstadia.dll` across games and reboots.
- **Multilingual Interface (`UK / EN / DE / RU`):** Instant 4-language selector (**Українська**, **English**, **Deutsch**, **Русский**) persisted in Windows Registry and Web Studio.
- **Zero-SDK Build:** `StadiaRumble.bat` compiles `StadiaRumble.exe` automatically using Windows' built-in `.NET Framework 4.x` (`csc.exe`) with SHA-256 change detection and serves the signature Stadia taskbar icon.

---

## Requirements

- **OS:** Windows 10/11 **x64** (Build 22000 or newer; verified through Build **26300**).
- **Controller:** Google Stadia Controller switched to **Bluetooth LE mode** (or connected via USB-C).
- **Privileges:** Administrator rights (UAC) are required only when installing or uninstalling the driver.
- **Dependencies:** **None required beforehand.** No Visual Studio or WDK installation needed — `driver/build.ps1` downloads official Microsoft portable MSVC + Windows SDK + WDK files automatically when building from source, and `install.bat` / `manage.bat` cleans them up afterward.

---

## Quick Start (End Users)

1. Download the latest archive (`StadiaRumble-1.0.15-win-x64.zip`) from [**GitHub Releases**](https://github.com/wizardwiener/StadiaRumble/releases) and extract it to a folder.
2. Right-click **`install.bat`** (or **`manage.bat`**) → **Run as administrator**.
   - The script builds the latest `winstadia.dll` if needed, generates a one-time machine-local signing certificate (and immediately deletes the private key after signing), installs the driver package via `pnputil`, and compiles `StadiaRumble.exe`.
3. Connect your **Google Stadia Controller** over Bluetooth LE or USB.
4. Launch **`StadiaRumble.bat`** to test motors, view live BLE latency/battery charts, and save your `0xE2` / `0xE3` profiles to the driver — then close `[X]` whenever you are done.

To uninstall and restore the default Windows HID driver, run **`uninstall.bat`** as administrator (or `.\driver\install.ps1 -Uninstall`).

---

## Build From Source (Developers)

```powershell
# Clone repository
git clone https://github.com/wizardwiener/StadiaRumble.git
cd StadiaRumble

# 1. Build UMDF2 driver package (outputs to driver\out\pkg)
Set-ExecutionPolicy -Scope Process Bypass
.\driver\build.ps1

# 2. Install driver (prompts for UAC)
.\driver\install.ps1

# 3. Check live driver status in PowerShell
.\driver\status.ps1

# 4. Build & launch Stadia Rumble Studio
.\StadiaRumble.bat
```

---

## Architecture & Known Limitations

- **Private WinRT GATT Interface (`private-gatt.cpp`):** Windows' standard inbox `HidOverGatt` stack rejects BLE output reports (`0xC0070057`) because the Stadia controller's GATT output characteristic omits `Write Without Response`. To deliver rumble reliably, the driver communicates with `bthserv` through the same WinRT `GattClientDevice` interface used by Microsoft's inbox driver. Verified on Windows 11 builds **26200**, **26220**, and **26300**. A future major Windows Bluetooth stack rewrite could alter those internal vtable slots.
- **Self-Signed Local Package:** Because Windows requires driver packages to be signed, `install.ps1` creates a local certificate trusted only on your PC and permanently deletes its private key right after `winstadia.cat` is signed.
- **Event-Driven BLE Input:** In Bluetooth mode, the Stadia Controller transmits HID packets only when an axis or button changes (there is no idle heartbeat). During continuous stick movement, input updates run smoothly at the controller's **7.5 ms** BLE connection interval.

---

## Troubleshooting

- **Controller not visible in Studio:** Make sure Bluetooth is enabled, reconnect the gamepad, check the XInput slot selector (`#1–#4`), or run `.\driver\status.ps1`.
- **`Driver Settings (0xE2)` or `0xE3` unavailable:** An older version of `winstadia.dll` is active in the Windows Driver Store. Re-run `install.bat` as administrator to rebuild and update the driver package.
- **No rumble in games:** Verify `Strong/Weak Gain %` are above `0%` on the **Motors** tab, test **Pulse 1.5s** in Studio, and ensure vibration is enabled in the game's settings.
