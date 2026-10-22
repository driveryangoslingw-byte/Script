package online.wixed.boschusb;
public final class ProtocolSelfTest {
 private static void check(boolean yes,String label){if(!yes)throw new AssertionError(label);}
 public static void main(String[] args){
   byte[] hello=Protocol.handshake();
   check(hello.length==64,"hello length");
   check(hello[2]==3&&hello[3]==3&&hello[4]==2&&hello[5]==1,"hello bytes");
   byte[] expected={0,1,2};
   check(Protocol.summary(expected,0).equals("HANDSHAKE_SLIDING_WINDOW_A"),"handshake decode");
   check(Protocol.summary(new byte[]{0,(byte)255,0},0).equals("TRANSPORT_REJECTED_00_FF"),"real log error");
   byte[] q=Protocol.read(0x3a,0x0220,0);
   check(q.length==64&&(q[10]&255)==0x3a&&q[13]==0x22&&(q[14]&255)==2&&(q[15]&255)==0x20,"read packet");
   check(Protocol.hex(q).startsWith("02 00 07 00 FF FF 0B 00 00 00 3A 04 03 22 02 20"),"recorded request comparison");
   check(Protocol.summary(new byte[]{2,0,0x62,2,0x20,0x1a},0x0220).equals("UDS_POSITIVE_RAW_UNVERIFIED"),"positive decode");
   check(Protocol.summary(new byte[]{2,0,0x7f,0x22,0x31},0x0220).equals("UDS_NEGATIVE_0x31"),"negative decode");
   check(Protocol.summary(new byte[]{2,0,0x62,2,0x21},0x0220).equals("UNCLASSIFIED_RAW"),"not wrong DID");
   check(Protocol.dids(3).length==8&&Protocol.cid(3)==0x3b,"drive research profile");
   check(Protocol.dids(4).length==8&&Protocol.cid(4)==0x3c,"battery research profile");
   System.out.println("10 Bosch BES2 protocol tests PASS");
 }
}
