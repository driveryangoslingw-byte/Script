# Bosch BES2 Windows One-Button Diagnostic

Windows 10/11 x64. Native Windows HID 108C:0155, .NET 8 WinForms/HidSharp. No Python, no Android and no replacement USB driver.

## Use
1. Turn on the stationary BES2 bike, keep Intuvia installed, connect a USB **data** cable to Windows.
2. Run **BoschBES2Auto.exe** and press **ЗАПУСТИТЬ ВСЁ** once.
3. The app finds the device, checks the measured 65-byte Windows HID report structure (64 payload + Report ID 0), sends one confirmed 321 USB handshake, receives passively for 3 seconds, then issues **one read-only** CID 3A DID 0220 query.
4. The complete JSON report saves **automatically** into **Downloads/Bosch-BES2/**. Click **ПАПКА ОТЧЁТОВ** to see it. No extra save prompts.

IMPORTANT: Android tests on October 23, 2026 confirmed USB handshake 00 01 02 and raw 00 FF for custom read packets. 00 FF is a transport-level rejection whose specific meaning is **not known**; the app will not attempt random CID brute force, ECU security access, speed-limit editing, programming or firmware changes. Windows does **not** automatically solve Bosch's unpublished Sliding Window A state/routing/heartbeat. A trace of a working legitimate BES2 diagnostic session is needed to establish the missing protocol.

## Download

GitHub Actions > **Bosch BES2 Windows EXE** > latest successful run > artifact **Bosch-BES2-Windows-EXE** > unzip > run **BoschBES2Auto.exe**. The unsigned executable might trigger Windows SmartScreen. Do **not** disable antivirus or bypass a malware warning; check the source and report/scan the file first.

Raw USB log may contain serial numbers and identifiers. Redact before sending to others.

Build: dotnet publish windows/BoschBES2Auto/BoschBES2Auto.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Tests: dotnet run --project windows/BoschBES2.Tests/BoschBES2.Tests.csproj