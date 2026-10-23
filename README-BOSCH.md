# Bosch BES2 Diagnostic Lab 2.1 — bounded auto discovery (Android)

Offline, free Android USB-Host diagnostic application for **Bosch Intuvia 108C:0155**. The hardware must be your own bike, stopped and safely supported. This version cannot modify firmware or speed limits.

## New: AUTO discovery
- Button: **AUTO · Discover published CIDs (bounded read-only probe)**.
- Checks only seven community-reported addresses: `20 22 39 3A 5D 5E 5F`, not all 256 values.
- Starts handshake, then sends standard UDS **TesterPresent (3E 00)** to each candidate. Note: this is a session keepalive, **not necessarily the same Bosch heartbeat** used by the independent researcher. Exact original Bosch heartbeat frame has not been published in the cited excerpt.
- Only if a plausible TesterPresent response (`7E 00`) is seen, requests a single known DID using service `22`. Stops at the first matched DID signature, flagged **RAW_UNVERIFIED** until actual Bosch framing is confirmed.
- Stops after **three 00 FF transport rejections**, user cancellation, disconnect or completion; waits at least **1.2 seconds** between candidate probes.
- Never guesses authentication keys, unlocks security access, writes configurations, changes max assistance speed, clears faults or flashes firmware.

## Existing diagnostics
USB permission handling on Android, 64-byte USB framing, Bosch handshake, manually selected BUI, motor and battery profiles, JSON log export, protocol response classification and raw USB trace.

## Run
Download the `Bosch-BES2-Diagnostic-APK` artifact from the latest successful GitHub Action in the `bosch-usb-tablet` branch. Unzip and install `app-debug.apk`. Plug the USB cable between the powered display and Xiaomi Pad 5, accept the Android USB prompt, press **Find Bosch / Connect**, then **AUTO**. **Do not ride the bike while scanning.** If it reports transport errors, save JSON rather than hammering Retry.

## Known limitations
The user's Bosch hardware has consistently returned `00 FF` to DID requests despite a successful handshake. No evidence yet that any address or request format will successfully read a DID on this firmware. **The APK being built and unit tests passing cannot establish hardware compatibility.** Repeated `00 FF` can mean session/framing failure, not merely incorrect CID. The auto mode is a bounded experiment, not exhaustive brute force.
User-supplied logs may contain personally identifying device identifiers; remove them before publishing.

## References
- https://id.scribd.com/document/810278403/bosch (community BES2 transport and candidate CIDs)
- https://canbird.com/encyclopedia/uds/services/tester-present (standard UDS 0x3E behavior)
