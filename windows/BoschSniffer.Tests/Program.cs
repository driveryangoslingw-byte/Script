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
        void Record(byte[] payload, byte endpoint = 0x01)
        {
            const ushort hdr = 27;
            uint length = (uint)(hdr + payload.Length);
            w.Write(1760000000u); w.Write(510000u);
            w.Write(length); w.Write(length);
            w.Write(hdr); w.Write((ulong)1); w.Write(0u); w.Write((ushort)9);
            w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)5);
            w.Write(endpoint); w.Write((byte)1); w.Write((uint)payload.Length);
            w.Write(payload);
        }
        byte[] desc = new byte[] { 18,1,0,2,0,0,0,64,0x8C,0x10,0x55,0x01,0,1,1,2,3,1 };
        Record(desc, 0x80);
        Record(new byte[]{0,0,3,3,2,1},0x01);
        Record(new byte[]{0,1,2},0x81);
        Record(new byte[]{0,255},0x81);
    }
    var capture = PcapDecoder.Read(temp);
    Assert(capture.Records == 4, "four records parsed");
    Assert(capture.Frames.Count == 4, "four valid USB frames");
    Assert(capture.Frames[0].Note.Contains("108C:0155"), "Bosch VID/PID descriptor parsed");
    Assert(capture.Frames[1].Note.Contains("handshake request"), "321 request recognized");
    Assert(capture.Frames[2].Note.Contains("handshake reply"), "00 01 02 recognized");
    Assert(capture.Frames[3].Note.Contains("00 FF"), "00 FF not misclassified as ECU read");
    Assert(capture.Frames.TrueForAll(x=>x.BoschCandidate), "descriptor links device to all frames");
    Assert(!capture.PartialTail, "complete file");
    using (var f = File.Open(temp, FileMode.Append)) f.WriteByte(0x77);
    Assert(PcapDecoder.Read(temp).PartialTail, "partial PCAP record tolerated");
    bool failed = false;
    try { PcapDecoder.Read(temp + ".missing"); } catch(FileNotFoundException) { failed = true; }
    Assert(failed, "missing file reported");
    Console.WriteLine("All BoschSniffer PCAP parser tests passed.");
}
finally { try { File.Delete(temp); } catch { } }
