# BLE Lab Beacon for Flipper Zero — Unleashed 094
Source for a time-limited, low-power, non-connectable BLE advertising compatibility test.
Controls: OK start/stop; UP select 1000/500/200ms; LEFT burst/steady; RIGHT payload; BACK exit.
Automatically stops after 60 seconds. Does not pair or send HID commands.
Runs 2-second burst windows with 2-second gaps in Burst mode.
Build: install uFBT and run `ufbt update --url=https://github.com/DarkFlippers/unleashed-firmware/releases/download/unlshd-094/flipper-z-f7-sdk-unlshd-094.zip` then `ufbt`.
The GitHub Actions workflow uploads a compiled .fap artifact on successful builds.
