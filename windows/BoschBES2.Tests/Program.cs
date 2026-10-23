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
Console.WriteLine($"{assertions} protocol assertions PASS");
