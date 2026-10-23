# Bosch USB Packet Inspector 1.0

Native Windows 10/11 x64 USB capture GUI using **the installed USBPcap filter driver**, not Wireshark. The app does **not** emulate a kernel driver and never sends commands to Bosch.

## One-time setup
1. Install official [USBPcap](https://desowin.org/usbpcap/) and reboot. The optional Wireshark application is **not** required.
2. Start `BoschUsbSniffer.exe` as administrator (Windows prompts once automatically).
3. Turn on the bike/Intuvia, plug it into a **data** USB port.
4. Select AUTO USB hubs and click **НАЧАТЬ ЗАХВАТ**. The capture covers other USB devices on those hubs, so keep unrelated USB devices idle.
5. While recording, use a legitimately available Bosch diagnostic application if you have access, or run the existing `BoschBES2Auto.exe` to record its 00 FF exchange.
6. Click **СТОП + АНАЛИЗ**, inspect packets, select to view HEX, and optionally export the *Bosch candidate only* JSON.
7. Full `.pcap` files live in `Downloads/Bosch-BES2/Captures/<session>/`. You can import previous USBPcap classic PCAP files without installing the driver.

## Implementation
- Runs installed USBPcapCMD with `-d \\.\USBPcapN -A --inject-descriptors -o -`, and pipes raw output into .pcap to avoid unflushed stdout on abrupt stop. USBPcap streams are per-hub.
- The capture UI enumerates filter handles by opening `\\.\USBPcap1...`; when the USBPcap filter is missing it provides a clear error.
- USBPcap classic DLT 249 reader interprets the 27-byte pseudoheader and USB transfer data, recognizes observed Bosch VID/PID 108C:0155, 321 handshake, `00 01 02`, `00 FF`, and flags **speculative** framed packets without claiming Bosch ECU values.
- Capture processes are terminated on Stop; the final packet in an interrupted stream can be incomplete, and the parser gracefully ignores truncated tails.
- Capturing is **passive**: no motor unlock, speed limit changes, firmware writes, UDS brute force, or spoofed heartbeats.

Privacy: USB capture can include traffic from unrelated devices and device serials. Inspect/redact reports and captures before sharing; the program never uploads anything. A USB trace of the *same failing app* only confirms our existing 00 FF exchange; discovering the missing successful session framing requires comparing with an authentic, authorized working diagnostic session.

## Build
```powershell
dotnet run --project windows/BoschSniffer.Tests/BoschSniffer.Tests.csproj -c Release
dotnet publish windows/BoschUsbSniffer/BoschUsbSniffer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false
```
