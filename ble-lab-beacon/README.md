# BLE Lab Multi — Unleashed 094

Purpose: measure BLE scanner discovery and deduplication using **four clearly labeled,
non-connectable test identities**. This does not forge manufacturer pairing payloads,
trigger Apple pop-ups, pair to phones or jam nearby Bluetooth traffic.

- Runs at most **60 seconds**. Press OK to stop earlier; BACK quits and stops TX.
- The **single extra beacon radio** alternates 4 identities (`FZLAB-01` ...
  `FZLAB-04`) every 5 seconds with four distinct locally derived random addresses.
  The identities are **not** transmitted simultaneously.
- Advertising interval defaults to 100ms; UP cycles 500/200/100ms.
- Nominal 0 dBm RF power using Unleashed extra beacon API.
- LEFT toggles steady versus 2s on / 2s off burst.
- RIGHT compares one identity vs four identities; only while stopped.
- Restores earlier extra-beacon configuration and payload on exit when possible.

**On iPhone:** use an actual BLE scanner, e.g. nRF Connect. Normal iOS
Settings > Bluetooth may not list these non-connectable beacons. Scanner
OS behavior varies and entries may be deduplicated.

Build is provided via GitHub Actions against the `unlshd-094` SDK.
Compilation does not establish real-device compatibility. Verify that your
Flipper is running the corresponding SDK-compatible firmware.
