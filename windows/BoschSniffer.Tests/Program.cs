using System;
using System.IO;
using BoschUsbSniffer;

static void Assert(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS " + label);
}
string temp = Path.Combine(Path.GetTempPath(), "bosch-usbpcap-test-" + Guid.NewGuid().ToString("N") + ".pcap");
try
{
    using (var f = File.Create(temp))
    using (var w = new BinaryWriter(f))
    {
        w.Write(0xA1B2C3D4u);
        w.Write((ushort)2); w.Write((ushort)4);
        w.Write(0); w.Write(0); w.Write(65535); w.Write(249);
        void Record(byte[] payload, byte endpoint = 0x01, byte transfer = 1, ushort device = 5)
        {
            ushort hdr = (ushort)(transfer == 2 ? 28 : 27);
            uint length = (uint)(hdr + payload.Length);
            w.Write(1760000000u); w.Write(510000u);
            w.Write(length); w.Write(length);
            w.Write(hdr); w.Write((ulong)1); w.Write(0u); w.Write((ushort)9);
            w.Write((byte)0); w.Write((ushort)1); w.Write(device);
            w.Write(endpoint); w.Write(transfer); w.Write((uint)payload.Length);
            if (transfer == 2) w.Write((byte)1); // USBPcap control DATA stage
            w.Write(payload);
        }
        byte[] desc = new byte[] { 18,1,0,2,0,0,0,64,0x8C,0x10,0x55,0x01,0,1,1,2,3,1 };
        Record(desc, 0x80, transfer: 2);
        Record(new byte[]{0,0,3,3,2,1},0x01);
        Record(new byte[]{0,1,2},0x81);
        Record(new byte[]{0,255},0x81);
        Record(desc, 0x81, device: 6); // descriptor-like unrelated interrupt payload
        Record(new byte[]{0xA5}, 0x81, device: 6);
        Record(desc.Take(12).ToArray(), 0x80, transfer: 2, device: 7);
        Record(new byte[]{0xA5}, 0x81, device: 7);
    }
    var capture = PcapDecoder.Read(temp);
    Assert(capture.Records == 8, "eight records parsed");
    Assert(capture.Frames.Count == 8, "eight valid USB frames");
    Assert(capture.Frames[0].Note.Contains("108C:0155"), "Bosch VID/PID descriptor parsed");
    Assert(capture.Frames[1].Note.Contains("handshake request"), "321 request recognized");
    Assert(capture.Frames[2].Note.Contains("handshake reply"), "00 01 02 recognized");
    Assert(capture.Frames[3].Note.Contains("00 FF"), "00 FF not misclassified as ECU read");
    Assert(capture.Frames.Take(4).All(x=>x.BoschCandidate), "control descriptor links Bosch device to all its frames");
    Assert(capture.Frames.Skip(4).All(x=>!x.BoschCandidate), "unrelated and truncated descriptor signatures cannot identify another device");
    Assert(capture.Frames[0].TransferName == "CTRL" && capture.Frames[0].HeaderLength == 28,
        "descriptor fixture uses a valid USBPcap control data header");
    Assert(!capture.PartialTail, "complete file");
    using (var f = File.Open(temp, FileMode.Append)) f.WriteByte(0x77);
    Assert(PcapDecoder.Read(temp).PartialTail, "partial PCAP record tolerated");
    bool failed = false;
    try { PcapDecoder.Read(temp + ".missing"); } catch(FileNotFoundException) { failed = true; }
    Assert(failed, "missing file reported");

    byte[] deviceDescriptor = { 18,1,0,2,0,0,0,64,0x8C,0x10,0x55,0x01,0,1,1,2,3,1 };
    PcapDecoder.Identify(deviceDescriptor, 3, out bool bulkCandidate, out bool bulkDescriptor);
    Assert(!bulkCandidate && !bulkDescriptor, "VID/PID bytes in bulk data are not a device descriptor");
    PcapDecoder.Identify(deviceDescriptor.Take(17).ToArray(), 2, out bool shortCandidate, out bool shortDescriptor);
    Assert(!shortCandidate && !shortDescriptor, "all 18 descriptor bytes must be captured");
    PcapDecoder.Identify(deviceDescriptor, 2, out bool completeCandidate, out bool completeDescriptor);
    Assert(completeCandidate && completeDescriptor, "complete control descriptor remains recognized");

    byte[] buiRequest = new byte[64];
    Convert.FromHexString("02000000FFFF0B00003A0403220220").CopyTo(buiRequest,0);
    string requestNote = PcapDecoder.Identify(buiRequest,1,out bool requestCandidate,out bool requestDescriptor);
    Assert(requestCandidate && !requestDescriptor && requestNote.Contains("SWA"),
        "firmware-derived request layout is a candidate without establishing VID/PID");
    byte[] buiReply = new byte[64];
    Convert.FromHexString("0200000008980201003D080762022005090200").CopyTo(buiReply,0);
    Array.Fill(buiReply,(byte)0xAA,19,45); // synthetic padding, never captured live RAM
    string replyNote = PcapDecoder.Identify(buiReply,1,out bool replyCandidate,out bool replyDescriptor);
    Assert(replyCandidate && !replyDescriptor && replyNote.Contains("software reply"),
        "observed BUI software reply layout is a candidate with its tail ignored");
    buiReply[2]=1;
    PcapDecoder.Identify(buiReply,1,out bool staleCandidate,out _);
    Assert(!staleCandidate,"later device sequence cannot masquerade as the known initial reply");
    buiReply[2]=0;buiReply[9]=0x39;
    PcapDecoder.Identify(buiReply,1,out bool wrongCidCandidate,out _);
    Assert(!wrongCidCandidate,"unrelated CID cannot match the known BUI reply signature");
    byte[] legacyPacket = new byte[64];
    Convert.FromHexString("02000700FFFF0B0000003D0403620220").CopyTo(legacyPacket,0);
    PcapDecoder.Identify(legacyPacket,1,out bool legacyCandidate,out _);
    Assert(!legacyCandidate,"previous invalid framed layout is no longer tagged as BES2");
    Console.WriteLine("All BoschSniffer PCAP parser tests passed.");
}
finally { try { File.Delete(temp); } catch { } }
