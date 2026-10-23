# Bosch BES2 One-Tap USB Diagnostic Lab 4.3

This Android APK performs an **automatic, strictly read-only research session** with a Bosch BES2 Intuvia/HMI USB interface (VID:PID `108C:0155`). It is not a Bosch BES3 Smart System tool.

## ONE BUTTON: Start Automatic Diagnosis

Connect the powered-on **stationary** bike to the Android device with a data-capable USB cable, open the app and tap **START AUTOMATIC DIAGNOSIS**. Accept the Android USB permission prompt if shown. The rest runs without manually selecting CIDs, handshakes or individual actions:

1. Find and open the HID interface, logging IN/OUT endpoint characteristics.
2. Fetch the standard USB HID report descriptor and extract report IDs (this does not contact the ECU).
3. Negotiate the **three published transport preference orders** `321`, `213`, `123`. The app closes and re-opens the USB interface between patterns (not a physical bicycle power cycle).
4. Re-select `321`, confirm **Sliding Window A**, then passively listen in two 450-ms USB windows.
5. If and only if Sliding Window A was negotiated, send **one** published **UDS ReadDataByIdentifier** packet to legacy BMS CID `3C`, DID `F130` (`02 00 07 00 FF FF 0B 00 00 00 3C 04 03 22 F1 30`, padded to 64 bytes).
6. Categorize the reply, **immediately stop** on `00 FF`, unexpected or unverified packets; store JSON internally at `bosch-bes2-last-diagnostic.json`. Use **Save diagnostic JSON to a file** to export the report to a user-selected folder.

**Important:** The automatic check is deliberately bounded, not a brute-force attack on the motor. It does not try unknown heartbeat bytes, guessed CID routes, ECU writes, programming, UDS SecurityAccess, resets, firmware changes, or speed limiter modifications. A successful USB handshake **does not demonstrate** an established ECU diagnostic session. A `00 FF` reply does **not** identify whether routing, sequence state, ECU availability, or transport framing was the cause.

Previous hardware observations:
- USB IN/OUT: 64-byte interrupt reports on endpoints `81` / `01`; report descriptor contains no HID Report IDs.
- `321` → `00 01 02` (Sliding Window A).
- `213` → `00 01 02` (Sliding Window A).
- `123` → `00 01 01` (Legacy).
- The published direct BMS sample and selected BUI diagnostic requests returned `00 FF`.
- No unsolicited USB packets arrived during earlier passive listening.

If an automatic run returns `00 FF` again, the **protocol transport layer remains unsolved**. A real Bosch diagnostic USB capture from an authorized session or verified source implementation would be required to identify missing heartbeat/routing/sequence behavior. Do not infer a fix from sending random packets.

## Download & source

Open the latest successful **Bosch BES2 Android APK** GitHub Actions run on branch `bosch-usb-tablet`, download the `Bosch-BES2-Diagnostic-APK` artifact, extract and install the debug APK. This uses Android's `USB Host` API and does not require internet or permissions beyond USB device access.

Source reference: https://de.scribd.com/document/810278403/bosch (sections *Handshake*, *Erste Anfrage*, *Daten / Request*, *Geräte*).

**Privacy:** Diagnostic JSON contains raw USB data and may include device identifiers/serials. Review and redact before posting.
