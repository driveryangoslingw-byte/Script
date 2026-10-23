# Bosch BES2 USB Investigation 4.4

Read-only Android USB test for the **older BES2 Intuvia** USB HID interface `108C:0155`. BES3 Smart System tooling and procedures **do not apply**.

## What the new ONE BUTTON does

With the bike **powered on and Intuvia mounted**, connect by USB data cable and press **START AUTOMATIC DIAGNOSIS**.

1. Record USB HID report descriptor and endpoint report sizes (standard USB only).
2. Send the **observed successful** `321` handshake *once*. Your USB replies: `00 01 02`, Sliding Window A.
3. Capture USB IN for 3 seconds without sending ECU messages, logging any unsolicited packets.
4. Send **one** documented-format `ReadDataByIdentifier(0x0220)` addressed to current-era **BUI CID `3A`**, rather than obsolete old BMS **CID `3C`**. On a raw `00 FF` reply, stop immediately.
5. Show concise conclusion and save diagnostic JSON inside the app; export using **Save diagnostic JSON to a file**.

**Why change this?** The public Bosch eBike HIM document explicitly warns that old software CIDs (`3A/3B/3C` as *the old request/reply mapping*) may no longer be valid and reports BUI responses to `3A` in the observed newer CID mapping. A 4.3 auto-run used the old `3C` example as a default and reported `00 FF`. Earlier manual tests of BUI `3A` also gave `00 FF`, so this update does **not** claim to solve the missing Bosch proprietary session/routing layer. It removes misleading redundant handshake permutations and produces a more targeted diagnostic artifact.

## Verified hardware observations (screenshots from 23 October 2026)

- HID interrupt IN `0x81`, OUT `0x01`, 64-byte reports, no report ID in 29-byte HID descriptor.
- `321` → `00 01 02` (Sliding Window A); `213` → `00 01 02`; `123` → `00 01 01` (Legacy).
- Direct BUI CID `3A` DID `0220` and published legacy BMS CID `3C` DID `F130` returned raw `00 FF`.
- Passive receive previously saw no unsolicited HID traffic; longer capture helps test whether this persists.
- User confirmed controller mounted and bike switched on while testing.

`00 FF` is not a validated UDS Negative Response, does not prove the CID is wrong, and cannot identify whether missing frame state, routing, initialization, or session establishment caused the error. An original successful **BES2** USB capture would be required to reproduce unknown proprietary handshake/heartbeat traffic accurately. Do not try brute-force CIDs, security unlocks, write commands or firmware changes.

## Safety

No parameter writes, UDS SecurityAccess, resets, firmware flashing or speed-limit modifications. No random or high-volume probing. Use only on your own bike, stationary. Exports can contain identifiers and serial data: redact before sharing.

## Download & source

Open the latest successful **Bosch BES2 Android APK** GitHub Actions run on branch `bosch-usb-tablet`, download the `Bosch-BES2-Diagnostic-APK` artifact, extract and install the debug APK. This uses Android's `USB Host` API and does not require internet or permissions beyond USB device access.

Source reference: https://de.scribd.com/document/810278403/bosch (sections *Handshake*, *Erste Anfrage*, *Daten / Request*, *Geräte*).

**Privacy:** Diagnostic JSON contains raw USB data and may include device identifiers/serials. Review and redact before posting.
