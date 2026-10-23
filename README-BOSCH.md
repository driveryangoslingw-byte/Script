# Bosch BES2 USB Transport Lab 4.1 — Android (read-only)

Experimental tool for **Bosch BES2 Intuvia** HID device `108C:0155`, not Bosch BES3 / Smart System. **USB handshake confirmed on user hardware; ECU diagnostics are not yet confirmed.**

## New in 4.1 — test the real published command directly
Version 4.0 never reached the DID read when a speculative TesterPresent failed. The published Bosch eBike HIM document actually shows a direct ISO-TP ReadDataByIdentifier frame without a prerequisite heartbeat and says the first empty request is optional.

Added three independent read-only actions:

- **3 · DIRECT read / selected CID (no heartbeat):** handshake `00 00 03 03 02 01`, await `00 01 02`, then exactly one `0x22` read on selected profile with sequence 0. No empty packet and no TesterPresent; never brute-force addresses.
- **4 · PUBLISHED BMS example:** handshake, then **exactly one** public reference frame (64 bytes padded with zeros): `02 00 07 00 FF FF 0B 00 00 00 3C 04 03 22 F1 30`. This reads the BMS part-number DID `F130` from legacy CID `3C`. This **may not respond** on the user's newer software; it is a faithful *wire-format reference*, not a guaranteed working channel.
- **7 · Inspect USB HID report descriptor:** standard USB GET_DESCRIPTOR (report type 0x22) on the claimed interface. No UDS command, no CAN traffic. Parses report-ID items properly and displays interface/endpoint packet sizes. Some devices may reject that descriptor read without affecting other functions.

**5 · Guided handshake / tester / DID** remains available for comparison, but the `3E 00` keepalive is only a candidate, **not a verified Bosch-specific heartbeat**. **6 · Passive receive** listens with no diagnostic payloads.

All active probes send **at most one ECU diagnostic request per click** (guided additionally sends one experimental TesterPresent and only proceeds after a plausible positive frame), stop on unknown responses and do not try other CIDs automatically. A `00 FF` reply is classified as a USB transport-level error; its detailed cause is unknown and must not be misreported as UDS Negative Response or an incorrect CID.

## Install and test
Open the latest successful GitHub Actions workflow **Bosch BES2 Android APK** for branch `bosch-usb-tablet`. Download artifact `Bosch-BES2-Diagnostic-APK`, unzip and install `app-debug.apk`. Connect the powered stationary bike to your Android tablet via USB data cable.

For the next test: `1 Find/Connect` -> `7 Inspect USB HID report descriptor` -> `3 DIRECT read` with Display BUI CID 3A -> `4 PUBLISHED BMS example`. Each direct action negotiates a fresh handshake and sends just one DID read. Report the raw HEX and complete status from each. Redact device serial numbers before sharing JSON.

## Technical and safety limitations
- Live hardware so far: 64-byte interrupt transfers, VID:PID `108C:0155`, OUT endpoint `01`, IN `81`, USB handshake accepted `00 01 02`.
- A previous published empty packet and TesterPresent candidate on CID `3A` returned `00 FF`. Passive mode returned zero unsolicited frames.
- Transport session machinery, response framing, true routing, and original Bosch heartbeat remain incompletely reverse engineered. **This build does not claim a working diagnostic reader or repaired bicycle.**
- No motor parameter writes, resets, firmware changes, unlocks or security-access requests. Keep bicycle stationary while plugged in.
- Saved diagnostic JSON may include hardware identifiers, serial numbers and private data.

## Protocol reference
Bosch eBike HIM community document: https://id.scribd.com/document/810278403/bosch — relevant sections *USB-Protokoll*, *Erste Anfrage*, *Daten / Request* and *Geräte*.
