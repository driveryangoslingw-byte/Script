using System;
using System.Linq;
namespace BoschBES2Auto;
public static class Protocol
{
 public const int Vid=0x108C, Pid=0x0155, Len=64;
 public static byte[] Handshake() {
  var b=new byte[Len];b[2]=3;b[3]=3;b[4]=2;b[5]=1;return b;
 }
 public static byte[] ReadBui() => InitialSwaTransport.BuildBuiRead(0x0220);
 public static byte[] ToWindowsReport(byte[] raw,int size) {
  if(raw.Length!=Len || size!=Len+1)throw new InvalidOperationException("HID output length mismatch. Expected 65 bytes including Report ID.");
  var b=new byte[size];Buffer.BlockCopy(raw,0,b,1,Len);return b;
 }
 public static byte[] FromWindowsReport(byte[] buf,int size) {
  if(size!=Len+1||size>buf.Length||buf[0]!=0)throw new InvalidOperationException("Unexpected HID report ID or length. Expected exactly 65 bytes with Report ID 0.");
  return buf.Skip(1).Take(Len).ToArray();
 }
 public static string HandshakeResult(byte[] b) {
  if(b.Length>=3&&b[0]==0&&b[1]==1) return b[2] switch {
   1=>"HANDSHAKE_LEGACY",2=>"HANDSHAKE_SLIDING_WINDOW_A",3=>"HANDSHAKE_SLIDING_WINDOW_B",_=>"HANDSHAKE_UNKNOWN_MODE"};
  if(b.Length>=2&&b[0]==0&&b[1]==255)return "HANDSHAKE_ERROR_00_FF";
  return "HANDSHAKE_UNKNOWN_REPLY";
 }
 public static bool TryReadBuiSoftwareVersion(byte[] raw,out string version) {
  version="";
  // Support only the initial BUI-local reply verified in firmware and on
  // hardware. b4..5 are available queue capacity, not a constant sentinel.
  // b6 is device TX sequence + 2; b7 is the special BUI-local flag.
  if(!InitialSwaTransport.TryReadPositiveBui(raw,0x0220,out var value)||value.Length!=4)return false;
  version=$"{value[0]}.{value[1]}.{value[2]}.{value[3]}";
  // Bytes 19..63 are outside the CAN record and may contain stale RAM.
  return true;
 }
 public static string ReadResult(byte[] b) {
  if(b==null)return "UNVERIFIED_DATA_FRAME";
  if(b.Length==Len&&b[0]==0&&b[1]==255)return "TRANSPORT_00_FF_UNDECODED";
  if(TryReadBuiSoftwareVersion(b,out _))return "BUI_SOFTWARE_VERSION_READ";
  if(InitialSwaTransport.TryReadBuiSingleFrame(b,out var uds)&&uds.Length==3&&uds[0]==0x7F&&uds[1]==0x22)
   return $"UDS_NEGATIVE_0x{uds[2]:X2}";
  return "UNVERIFIED_DATA_FRAME";
 }
 public static string Hex(byte[] b)=>BitConverter.ToString(b).Replace('-',' ');
}

