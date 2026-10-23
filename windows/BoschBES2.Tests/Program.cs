using BoschBES2Auto;

int assertions=0;
void Assert(bool value,string message){assertions++;if(!value)throw new Exception(message);}
void RejectReport(byte[] bytes,int count,string message) {
 bool rejected=false;
 try { Protocol.FromWindowsReport(bytes,count); }
 catch(InvalidOperationException) { rejected=true; }
 Assert(rejected,message);
}
void RejectVersion(byte[] frame,string message) {
 Assert(!Protocol.TryReadBuiSoftwareVersion(frame,out string value)&&value=="",message);
 Assert(Protocol.ReadResult(frame)=="UNVERIFIED_DATA_FRAME",message+" remains unverified");
}
// Hardware-observed header and CAN record, 2026-10-23, with the Windows
// Report ID removed. Bytes 19..63 are synthetic AA padding: live RAM is
// deliberately excluded from this public fixture and must never be decoded.
byte[] response=new byte[64];
Convert.FromHexString("0200000008980201003D080762022005090200").CopyTo(response,0);
Array.Fill(response,(byte)0xAA,19,45);

Assert(Protocol.Handshake().Length==64,"handshake length");
Assert(Protocol.Hex(Protocol.Handshake()).StartsWith("00 00 03 03 02 01"),"321 handshake");
var request=Protocol.ReadBui();
Assert(Protocol.Hex(request).StartsWith("02 00 00 00 FF FF 0B 00 00 3A 04 03 22 02 20"),"firmware-validated sequence zero / slot eight BUI read");
Assert(request.Length==64&&request.Skip(15).All(x=>x==0),"request is zero padded to exactly 64 bytes");
var win=Protocol.ToWindowsReport(request,65);
Assert(win.Length==65&&win[0]==0&&win[1]==2,"Windows Report ID");
Assert(Protocol.FromWindowsReport(win,65).SequenceEqual(request),"Windows report round trip preserves all 64 payload bytes");
var input=new byte[65];input[2]=1;input[3]=2;
Assert(Protocol.HandshakeResult(Protocol.FromWindowsReport(input,65))=="HANDSHAKE_SLIDING_WINDOW_A","strip Windows Report ID");
RejectReport(input,64,"short Windows read cannot lose an opcode by stripping byte zero");
RejectReport(new byte[64],65,"reported size must fit input buffer");
RejectReport(new byte[66],66,"oversized Windows read is not a Bosch report");
input[0]=1;
RejectReport(input,65,"unexpected nonzero Report ID is rejected");
RejectReport(Array.Empty<byte>(),0,"empty Windows report is rejected");
Assert(Protocol.HandshakeResult(new byte[]{0,1,1})=="HANDSHAKE_LEGACY","legacy");
Assert(Protocol.HandshakeResult(new byte[]{0,1,3})=="HANDSHAKE_SLIDING_WINDOW_B","sliding B");
var error=new byte[64];error[1]=255;
Assert(Protocol.ReadResult(error)=="TRANSPORT_00_FF_UNDECODED","raw error");
Assert(response.Length==64,"sanitized hardware fixture contains all 64 bytes");
Assert(Protocol.TryReadBuiSoftwareVersion(response,out string version)&&version=="5.9.2.0","actual hardware response yields four decimal version bytes");
Assert(Protocol.ReadResult(response)=="BUI_SOFTWARE_VERSION_READ","only the supported BUI software read is recognized");
Assert(Protocol.FromWindowsReport(Protocol.ToWindowsReport(response,65),65).SequenceEqual(response),"nonzero received tail survives Windows report conversion");
RejectVersion(request,"outbound request or echo is not a response");
RejectVersion(new byte[]{0,1,2},"handshake is not an ECU read");
RejectVersion(response.Take(19).ToArray(),"matching record in short report is rejected");
RejectVersion(response.Take(63).ToArray(),"63-byte report is rejected");
RejectVersion(response.Concat(new byte[]{0}).ToArray(),"65-byte raw report is rejected");
RejectVersion(Array.Empty<byte>(),"empty raw report is rejected");

foreach(var mutation in new (int Offset,byte Value,string Label)[] {
 (0,0,"wrong transport type"),(1,1,"wrong reserved header byte"),
 (2,1,"stale device sequence"),(2,7,"old incorrect sequence seven"),
 (3,1,"wrong acknowledged host sequence"),(6,3,"wrong initial special reply length field"),
 (7,0,"missing BUI-local flag"),(7,2,"unsupported BUI-local flag"),
 (8,1,"wrong CID high byte"),(9,0x3A,"request CID is not response CID"),
 (9,0x39,"unrelated response CID"),(10,7,"short CAN DLC"),(10,9,"oversized CAN DLC"),
 (11,6,"wrong ISO-TP length"),(11,0x10,"segmented ISO-TP"),
 (12,0x22,"request service is not positive response"),(12,0x7E,"wrong positive service"),
 (13,3,"wrong DID high byte"),(14,0x21,"wrong DID low byte")
}) {
 var changed=response.ToArray();changed[mutation.Offset]=mutation.Value;
 RejectVersion(changed,mutation.Label);
}
var shifted=response.ToArray();Array.Copy(response,8,shifted,9,11);shifted[8]=0;
RejectVersion(shifted,"shifted CAN record cannot be found by byte scanning");
var oldPacket=new byte[64];
Convert.FromHexString("02000700FFFF0B0000003D0403620220").CopyTo(oldPacket,0);
RejectVersion(oldPacket,"previous invalid framing is no longer accepted");
var padding=response.ToArray();Array.Fill(padding,(byte)0xFF,19,45);
Assert(Protocol.TryReadBuiSoftwareVersion(padding,out version)&&version=="5.9.2.0","bytes outside the one CAN record are never decoded");
padding[4]=0;padding[5]=0;
Assert(Protocol.TryReadBuiSoftwareVersion(padding,out version)&&version=="5.9.2.0","available queue capacity is not required to equal a magic value");
padding[15]=255;padding[16]=0;padding[17]=128;padding[18]=10;
Assert(Protocol.TryReadBuiSoftwareVersion(padding,out version)&&version=="255.0.128.10","version uses unsigned decimal components");

var negative=response.ToArray();negative[10]=4;negative[11]=3;
negative[12]=0x7F;negative[13]=0x22;negative[14]=0x31;
Assert(!Protocol.TryReadBuiSoftwareVersion(negative,out version)&&version=="","negative response is never a version");
Assert(Protocol.ReadResult(negative)=="UDS_NEGATIVE_0x31","correlated single-frame negative response");
negative[13]=0x3E;
Assert(Protocol.ReadResult(negative)=="UNVERIFIED_DATA_FRAME","negative reply for another service remains unverified");
negative[13]=0x22;negative[2]=1;
Assert(Protocol.ReadResult(negative)=="UNVERIFIED_DATA_FRAME","stale negative response remains unverified");
negative[2]=0;negative[9]=0x39;
Assert(Protocol.ReadResult(negative)=="UNVERIFIED_DATA_FRAME","negative reply from another CID remains unverified");

Assert(BuiResearch.Targets.Count >= 5 && BuiResearch.IsAllowed(0x0220) && BuiResearch.IsAllowed(0x0221),
    "bounded BUI research catalogue contains the verified baseline and selected read-only candidates");
Assert(!BuiResearch.IsAllowed(0x1234) && !BuiResearch.IsAllowed(0xF130),
    "unknown and BMS DIDs are forbidden in the BUI research workflow");
var researchRequest = BuiResearch.BuildReadRequest(0x0221);
Assert(researchRequest.Length == 64 && researchRequest[2] == 0 &&
    researchRequest[8] == 0 && researchRequest[9] == 0x3A &&
    researchRequest[12] == 0x22 && researchRequest[13] == 2 && researchRequest[14] == 0x21,
    "research encoder preserves hardware-confirmed SWA framing, CID and UDS service");
Assert(researchRequest.Take(13).SequenceEqual(Protocol.ReadBui().Take(13)) &&
    researchRequest.Skip(15).SequenceEqual(Protocol.ReadBui().Skip(15)),
    "the selected DID is the only wire byte changed from the verified request");
var otherReply = response.ToArray(); otherReply[14] = 0x21;
Assert(BuiResearch.TryPositiveRead(otherReply, 0x0221, out var researchRaw) &&
    researchRaw.SequenceEqual(new byte[] {5, 9, 2, 0}),
    "matching research DID returns only the four bytes inside the CAN record, not the trailing RAM");
Assert(!BuiResearch.TryPositiveRead(otherReply, 0x0220, out _),
    "research parser cannot attribute a response to another DID");
var stale = otherReply.ToArray(); stale[2] = 1;
Assert(!BuiResearch.TryPositiveRead(stale, 0x0221, out _),
    "stale sequence is rejected for the first SWA frame");
var wrongBuiCid = otherReply.ToArray(); wrongBuiCid[9] = 0x39;
Assert(!BuiResearch.TryPositiveRead(wrongBuiCid, 0x0221, out _),
    "unrelated response CID rejected");
var invalidDlc = otherReply.ToArray(); invalidDlc[10] = 7;
Assert(!BuiResearch.TryPositiveRead(invalidDlc, 0x0221, out _),
    "CAN DLC/ISO-TP length mismatch rejected");
var segmented = otherReply.ToArray(); segmented[11] = 0x10;
Assert(!BuiResearch.TryPositiveRead(segmented, 0x0221, out _),
    "unsupported segmented ISO-TP data is not accepted");
var malformed = otherReply.ToArray(); malformed[12] = 0x7F;
Assert(!BuiResearch.TryPositiveRead(malformed, 0x0221, out _),
    "UDS negative response is not a positive read");
bool rejectedUnknown = false;
try { BuiResearch.BuildReadRequest(0xF130); } catch (ArgumentOutOfRangeException) { rejectedUnknown = true; }
Assert(rejectedUnknown, "non-catalogued BMS request fails before any USB activity");


var odoResponse = response.ToArray();
odoResponse[14] = 0x49;
odoResponse[15] = 0x00; odoResponse[16] = 0x23;
odoResponse[17] = 0x80; odoResponse[18] = 0x5E;
Assert(BuiResearch.Targets.Single(t=>t.Did==0x0249).HardwareConfirmed,
    "BUI odometer 0249 is cross-checked with the actual Intuvia display");
Assert(BuiResearch.TryReadOdometer(odoResponse, out uint metres, out string kms) &&
    metres==2326622 && kms=="2326.6",
    "USB bytes 00 23 80 5E decode as 2,326,622 metres = 2326.6 km display");
var odoWrongDid = odoResponse.ToArray(); odoWrongDid[14] = 0x21;
Assert(!BuiResearch.TryReadOdometer(odoWrongDid, out _, out _),
    "wrong DID cannot be interpreted as odometer");
var odoShort = odoResponse.ToArray(); odoShort[10] = 7; odoShort[11] = 6;
Assert(!BuiResearch.TryReadOdometer(odoShort, out _, out _),
    "odometer requires exactly four metres bytes and an 8-byte CAN frame");
var odoNegative = odoResponse.ToArray(); odoNegative[12] = 0x7F;
Assert(!BuiResearch.TryReadOdometer(odoNegative, out _, out _),
    "a negative service response cannot become an odometer");
var odoMax = odoResponse.ToArray(); odoMax[15]=0xFF;odoMax[16]=0xFF;odoMax[17]=0xFF;odoMax[18]=0xFF;
Assert(BuiResearch.TryReadOdometer(odoMax, out metres, out kms) &&
    metres==uint.MaxValue && kms=="4294967.3",
    "unsigned big-endian metres counter decodes safely without signed overflow");

var hardwareResponse = response.ToArray();
hardwareResponse[14] = 0x21;
hardwareResponse[15] = 0; hardwareResponse[16] = 0; hardwareResponse[17] = 2; hardwareResponse[18] = 2;
Assert(BuiResearch.Targets.Single(t => t.Did == 0x0221).HardwareConfirmed,
    "BUI 0221 was observed on physical device and matches Bosch DiagnosticTool report");
Assert(BuiResearch.TryReadHardwareVersion(hardwareResponse, out string hardwareVersion) &&
    hardwareVersion == "0.0.2.2",
    "hardware version 00 00 02 02 decodes into 0.0.2.2");
var hardwareWrongDid = hardwareResponse.ToArray(); hardwareWrongDid[14] = 0x20;
Assert(!BuiResearch.TryReadHardwareVersion(hardwareWrongDid, out _),
    "DID 0220 cannot be misinterpreted as hardware version");
var hardwareNegative = hardwareResponse.ToArray(); hardwareNegative[12] = 0x7F;
Assert(!BuiResearch.TryReadHardwareVersion(hardwareNegative, out _),
    "negative UDS response does not produce hardware version");
var hardwareShort = hardwareResponse.ToArray(); hardwareShort[10] = 6; hardwareShort[11] = 5;
Assert(!BuiResearch.TryReadHardwareVersion(hardwareShort, out _),
    "hardware version requires exactly four bytes");
Console.WriteLine($"{assertions} protocol assertions PASS");
