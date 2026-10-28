# BLE HID Lab for Unleashed 094

A **single connectable BLE HID** for a voluntary pairing test with your own iPhone or Android device.

- On Flipper, launch Bluetooth > BLE HID Lab.
- In the phone's Bluetooth settings, pair with `LabHID ...` and accept the prompts.
- UP: transmit one Volume Up HID media-key press if connected.
- DOWN: transmit one Volume Down HID media-key press if connected.
- The program exits after 60 seconds (or press BACK).
- No advertised fake Apple products, no system popup impersonation, no unsolicited pairing attempts.
- No flood or attempted rate-limit bypass. This tests whether the phone accepts a genuine HID connection, **not** how it handles 5 requests in 5 milliseconds.

The previous non-connectable multi-beacon experiment is in the separate `ble-lab-beacon` project.
Only one BLE HID profile is active; it does not create four simultaneous connectable radios.

This source is built by GitHub Actions with the Unleashed `unlshd-094` SDK.
