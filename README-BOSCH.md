# Bosch BES2 Diagnostic Lab 2.0 (Android)
Free experimental diagnostic client for the user's **Bosch E-Bike HMI / Intuvia USB VID:PID 108C:0155**. Offline, local Android USB Host on a Xiaomi Pad 5.

### Scope
- Detect exact VID/PID and request USB permission with a **mutable PendingIntent**; previous build used IMMUTABLE and could report `USB permission denied` incorrectly on Android.
- Claim the matching interrupt HID interface; send/receive 64-byte frames.
- Confirm handshake `00 00 03 03 02 01` → `00 01 02`.
- Choose individually from 3 **research-observed BUI CID candidates** 3A/39/5D, or old/legacy DU CID 3B, BMS CID 3C. **These are not confirmed functional on this specific Bosch firmware.**
- Optional initial empty frame; UDS `0x22` reads only (no other requests).
- Read known published component DIDs. Real output is HEX and status, never guessed scaling, never invented Speed Limit. Stops immediately if `00 FF` occurs.
- Export a raw, local JSON diagnostic trace (possibly contains device identifiers). Clipboard and save-to-files supported.

### How to install
Actions → `Bosch BES2 Android APK` → last successful build → artifact `Bosch-BES2-Diagnostic-APK`; unzip then install APK. Connect powered bike and USB to tablet, allow Android USB prompt, press Find/Connect, then Handshake, then Read First DID. Try BUI CID 3A then 39 or 5D manually (new session / unplug between if needed). If `00 FF` persists do NOT repeatedly scan other addresses; export the diagnostic report.

### Test status
Compilation and a self-contained protocol packet test are checked by GitHub Actions, **not actual physical Bosch hardware**. No program can guarantee 100% working full diagnostics across proprietary BES2 firmware from this source alone.

### Sources
Community protocol research: https://id.scribd.com/document/810278403/bosch
BES3 software is *not compatible*: https://github.com/rweijnen/bosch-bes3-reader

### Privacy
Do not commit or share raw JSON logs publicly without removing serial numbers.
