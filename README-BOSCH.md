# Bosch BES2 USB Transport Lab 4.0 — Android (read-only)

Experimental diagnostics app for the older Bosch BES2 Intuvia HID `108C:0155` (NOT Bosch Smart System/BES3).

## What changed in 4.0
- Removed the UI for automatic CID discovery after the attached real-world log showed **all 21 combinations** rejected with `00 FF`.
- **Guided handshake / heartbeat candidate / DID** performs one bounded, read-only sequence for the selected CID:
  1. A single 64-byte Bosch HMI handshake (expect `00 01 02`, Sliding Window A).
  2. Optionally send the first empty data packet, sequence 0 (enabled by default). A `00 FF` or unrecognized packet stops the flow.
  3. UDS TesterPresent (`3E 00`) using sequence tracking. **This is only a candidate; Bosch's original heartbeat frame is not publicly confirmed.**
  4. Only after a framed candidate positive response, send one DID read (`22`) and log its raw response.
- **Passive receive** starts with handshake and then listens to USB IN for 4 short windows. It sends no diagnostic commands.
- Strict frame parsing checks envelope layout and ISO-TP single-frame length at known offsets, instead of scanning arbitrary bytes for `62`, `7F` or `7E`.
- Fail-closed session tracking prevents continued commands after an unknown transport reply.
- Reports carry explicit `heartbeat_protocol_verified=false` and `sliding_window_a_full_implementation_verified=false` flags. USB handshake success is not equivalent to a verified routed ECU session.

## Install
Go to the latest successful **Bosch BES2 Android APK** workflow for branch `bosch-usb-tablet`. Download the `Bosch-BES2-Diagnostic-APK` artifact ZIP, extract `app-debug.apk` and install it. Connect the powered bike and the Android tablet with a USB data cable. Do not ride the bike while connected.

## What has actually been observed
User-supplied logs from Bosch USB Lab 3.1:
- Host detects Bosch VID:PID `108C:0155`, USB OUT endpoint `0x01` and IN `0x81`.
- Host handshake `00 00 03 03 02 01 ...` responds `00 01 02 ...` — negotiated mode 2.
- An attempted empty frame, UDS TesterPresent and DID `0220` on many CIDs all return `00 FF`.
- That status is **not** a UDS Negative Response and its exact transport meaning is unverified. No working channel or DID read has been demonstrated.

### Limits
This build does not implement a fully reverse-engineered Sliding Window A transport or Bosch heartbeat discovery. Such a claim would require a genuine working Bosch DiagnosticTool USB capture or additional independent protocol documentation. It does **not** edit speed limits, write to ECUs, reset modules or guess access keys. It is a transport-analysis build, not a proven diagnostic or tuning tool.

Raw logs can contain device serial numbers and other identifying data. Review before sharing.

## Source
Bosch eBike HIM (community reverse engineering): https://id.scribd.com/document/810278403/bosch
