using System;
using System.Linq;
namespace BoschBES2Auto;
public static class Protocol
{
 public const int Vid=0x108C, Pid=0x0155, Len=64;
 public static byte[] Handshake() {
  var b=new byte[Len];b[2]=3;b[3]=3;b[4]=2;b[5]=1;return b;
 }
 public static byte[] ReadBui() {
  var b=new byte[Len];b[0]=2;b[2]=7;b[4]=255;b[5]=255;b[6]=11;
  b[10]=0x3A;b[11]=4;b[12]=3;b[13]=0x22;b[14]=2;b[15]=0x20;return b;
 }
 public static byte[] ToWindowsReport(byte[] raw,int size) {
  if(raw.Length!=Len || size!=Len+1)throw new InvalidOperationException("HID output length mismatch. Expected 65 bytes including Report ID.");
  var b=new byte[size];Buffer.BlockCopy(raw,0,b,1,Len);return b;
 }
 public static byte[] FromWindowsReport(byte[] buf,int size) {
  if(size<2||size>buf.Length||buf[0]!=0)throw new InvalidOperationException("Unexpected HID report ID or length.");
  return buf.Skip(1).Take(size-1).ToArray();
 }
 public static string HandshakeResult(byte[] b) {
  if(b.Length>=3&&b[0]==0&&b[1]==1) return b[2] switch {
   1=>"HANDSHAKE_LEGACY",2=>"HANDSHAKE_SLIDING_WINDOW_A",3=>"HANDSHAKE_SLIDING_WINDOW_B",_=>"HANDSHAKE_UNKNOWN_MODE"};
  if(b.Length>=2&&b[0]==0&&b[1]==255)return "HANDSHAKE_ERROR_00_FF";
  return "HANDSHAKE_UNKNOWN_REPLY";
 }
 public static string ReadResult(byte[] b) {
  if(b.Length>=2&&b[0]==0&&b[1]==255)return "TRANSPORT_00_FF_UNDECODED";
  if(b.Length<16||b[0]!=2||b[2]!=7||b[4]!=255||b[5]!=255)return "UNKNOWN_FRAME_NO_ECU_DATA";
  if(b[6]!=11||b[11]!=4||b[12]!=3)return "UNVERIFIED_HMI_FRAMING";
  if(b[13]==0x7F&&b[14]==0x22)return $"UDS_NEGATIVE_CANDIDATE_0x{b[15]:X2}";
  if(b[13]==0x62&&b[14]==2&&b[15]==0x20)return "DID_REPLY_CANDIDATE_UNVERIFIED";
  return "UNVERIFIED_DATA_FRAME";
 }
 public static string Hex(byte[] b)=>BitConverter.ToString(b).Replace('-',' ');
}