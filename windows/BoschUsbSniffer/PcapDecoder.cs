using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BoschUsbSniffer;

public sealed record UsbFrame(
    string Source, DateTimeOffset At, ushort Bus, ushort Device,
    byte Endpoint, byte Transfer, byte Info, uint Status, byte[] Data,
    ushort HeaderLength, string Note, bool BoschCandidate)
{
    public string Direction => (Endpoint & 0x80) != 0 ? "IN" : "OUT";
    public string TransferName => Transfer switch { 0 => "ISO", 1 => "INT", 2 => "CTRL", 3 => "BULK", 0xFE => "IRP", _ => "?" };
    public string DeviceKey => Source + "|" + Bus + "|" + Device;
    public string Hex => BitConverter.ToString(Data).Replace('-', ' ');
}

public sealed record PcapSummary(string File, List<UsbFrame> Frames, int Records, int InvalidRecords, bool PartialTail);

public static class PcapDecoder
{
    // Classic libpcap, LINKTYPE_USBPCAP=249. USBPcap pseudoheader fields are little-endian.
    public static PcapSummary Read(string path, int maxRecords = 30000)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 24) throw new InvalidDataException("PCAP header is missing.");
        var global = reader.ReadBytes(24);
        bool little;
        bool nano;
        if (global[0] == 0xD4 && global[1] == 0xC3 && global[2] == 0xB2 && global[3] == 0xA1) { little = true; nano = false; }
        else if (global[0] == 0xA1 && global[1] == 0xB2 && global[2] == 0xC3 && global[3] == 0xD4) { little = false; nano = false; }
        else if (global[0] == 0x4D && global[1] == 0x3C && global[2] == 0xB2 && global[3] == 0xA1) { little = true; nano = true; }
        else if (global[0] == 0xA1 && global[1] == 0xB2 && global[2] == 0x3C && global[3] == 0x4D) { little = false; nano = true; }
        else throw new InvalidDataException("Not a classic .pcap file (pcapng cannot be imported).");
        uint U32(ReadOnlySpan<byte> bytes) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if ((U32(global.AsSpan(20, 4)) & 0xFFFF) != 249)
            throw new InvalidDataException("The capture is not USBPcap (linktype must be 249).");
        var frames = new List<UsbFrame>();
        var candidateIndices = new HashSet<int>();
        var boschDevices = new HashSet<string>();
        int total = 0, invalid = 0;
        bool partial = false;
        string source = Path.GetFileName(path);
        while (total < maxRecords && stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 16) { partial = true; break; }
            var header = reader.ReadBytes(16);
            uint seconds = U32(header.AsSpan(0, 4));
            uint subseconds = U32(header.AsSpan(4, 4));
            uint included = U32(header.AsSpan(8, 4));
            total++;
            if (included > 2 * 1024 * 1024) { invalid++; break; }
            if (stream.Length - stream.Position < included) { partial = true; break; }
            byte[] record = reader.ReadBytes((int)included);
            if (record.Length < 27) { invalid++; continue; }
            ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0, 2));
            if (headerLength < 27 || headerLength > record.Length) { invalid++; continue; }
            ushort bus = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(17, 2));
            ushort device = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(19, 2));
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(10, 4));
            byte endpoint = record[21], transfer = record[22], info = record[16];
            uint statedDataLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(23, 4));
            int length = (int)Math.Min(statedDataLength, (uint)(record.Length - headerLength));
            if (length > 100_000) length = 100_000; // retain complete trace in .pcap, not giant payloads in memory
            byte[] data = record.AsSpan(headerLength, length).ToArray();
            string note = Identify(data, transfer, out bool candidate, out bool descriptor);
            if (descriptor) boschDevices.Add(source + "|" + bus + "|" + device);
            long ticks = nano ? subseconds / 100 : subseconds * 10L;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks); }
            catch (ArgumentOutOfRangeException) { time = DateTimeOffset.UnixEpoch; }
            var frame = new UsbFrame(source, time, bus, device, endpoint, transfer, info, status,
                data, headerLength, note, candidate);
            frames.Add(frame);
        }
        // Descriptor mapping is applied after all packets have been decoded.
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (boschDevices.Contains(f.DeviceKey))
                frames[i] = f with { BoschCandidate = true, Note = f.Note == "" ? "Bosch 108C:0155 device" : f.Note };
        }
        return new PcapSummary(path, frames, total, invalid, partial);
    }

    public static string Identify(byte[] bytes, byte transfer, out bool candidate, out bool descriptor)
    {
        candidate = false;
        descriptor = false;
        // Device descriptor: bLength=18, bDescriptorType=1, idVendor/idProduct at +8/+10
        for (int offset = 0; offset <= Math.Min(9, bytes.Length - 12); offset++)
        {
            if (bytes[offset] == 18 && bytes[offset + 1] == 1 &&
                bytes[offset + 8] == 0x8C && bytes[offset + 9] == 0x10 &&
                bytes[offset + 10] == 0x55 && bytes[offset + 11] == 0x01)
            {
                candidate = descriptor = true;
                return "Bosch USB descriptor VID:PID 108C:0155";
            }
        }
        // HID 65-byte report carries leading Report ID 00, USB payload itself may be 64 or 65 bytes.
        int start = bytes.Length >= 65 && bytes[0] == 0 ? 1 : 0;
        var span = bytes.AsSpan(start);
        if (span.Length >= 6 && span[0] == 0 && span[1] == 0 &&
            span[2] == 3 && span[3] == 3 && span[4] == 2 && span[5] == 1)
        {
            candidate = true;
            return "BES2 handshake request 321 (observed)";
        }
        if (span.Length >= 3 && span[0] == 0 && span[1] == 1 && span[2] == 2)
        {
            candidate = true;
            return "BES2 handshake reply 00 01 02 (observed)";
        }
        if (span.Length >= 2 && span[0] == 0 && span[1] == 0xFF)
        {
            candidate = true;
            return "BES2 transport 00 FF candidate (not decoded)";
        }
        if (span.Length >= 16 && span[0] == 2 && span[2] == 7 &&
            span[4] == 0xFF && span[5] == 0xFF)
        {
            candidate = true;
            return "BES2 speculative framed packet; CID " + span[10].ToString("X2");
        }
        return transfer == 2 ? "USB control transfer" : "";
    }
}
