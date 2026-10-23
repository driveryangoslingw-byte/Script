# Bosch BES2 USB Investigation 4.5 — one button

This native Android app is for the older Bosch BES2 Intuvia USB HID device `108C:0155`. BES3 is a different platform.

## How to use (one action)

1. Power on the stationary bicycle, leave Intuvia fitted, connect the USB **data** cable to the Android tablet.
2. Open the app and press **START ALL TESTS / ЗАПУСТИТЬ**. If Android requests access to the USB device, tap **Allow** (this system permission cannot be bypassed).
3. The app **automatically** records the standard USB HID report descriptor, negotiates the proven 321 USB handshake, captures three seconds of incoming USB packets, sends exactly one read-only BUI DID request (CID `3A`, DID `0220`), and stops on the first transport rejection.
4. **On Android 10+**, the JSON report is saved automatically in **Downloads/Bosch-BES2/**, with a timestamped name. A private backup is also kept inside the app. Manual report export is optional.

The STOP button remains available for user safety. No settings, CID choices, repeated retries or additional actions are required to gather the available evidence.

## What is and is not solved

The user-observed USB handshake `00 00 03 03 02 01` → `00 01 02` establishes **USB transport negotiation only**, not an ECU session. User's prior read attempts received `00 FF`, even after testing the optional empty frame, an experimental TesterPresent, the current BUI CID, and a historic BMS DID. The **exact cause** of `00 FF` is not documented. It could reflect missing proprietary Sliding Window A session handling, routing or packet state. This app does **not** invent a successful Bosch heartbeat, ECU response or access key, and it does not perform a blind brute-force CID sweep. Solving the proprietary layer needs a captured successful **BES2** diagnostic exchange or additional verified reverse-engineering evidence.

## Safety and privacy

Read-only requests only; no SecurityAccess, firmware flashing, resets, configuration or speed-limit changes. Reports can contain serials and raw identifiers, so review/redact before sharing or uploading. Windows-specific Wireshark/USBPcap capture is a separate workflow and cannot be transparently run on the Android tablet.

### Build and download

Open the latest successful APK workflow run on GitHub branch `bosch-usb-tablet`, download the artifact `Bosch-BES2-Diagnostic-APK`, extract and install the APK. The CI includes pure Java protocol tests. Because Android debug APKs are unsigned by a commercial publisher, install only from the connected source you trust; never disable Android malware protection just to force an installation.

Reference: [Bosch eBike HIM (research document)](https://de.scribd.com/document/810278403/bosch), sections *Handshake*, *Erste Anfrage*, *Daten*.
