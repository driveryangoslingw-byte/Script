package online.wixed.boschusb;

/** Pure-Java regression tests; no connected bike is required. */
public final class ProtocolSelfTest {
  private static int assertions=0;
  private static void check(boolean value,String message){
    assertions++;if(!value)throw new AssertionError(message);
  }
  private static byte[] frame(int declared,int cid,int length,int pci,int sid){
    byte[] f=new byte[Protocol.LEN];
    f[0]=2;f[2]=7;f[4]=(byte)255;f[5]=(byte)255;f[6]=(byte)declared;
    f[10]=(byte)cid;f[11]=(byte)length;f[12]=(byte)pci;f[13]=(byte)sid;
    return f;
  }
  public static void main(String[] args){
    byte[] handshake=Protocol.handshake();
    check(handshake.length==64,"handshake length");
    check(handshake[2]==3&&handshake[3]==3&&handshake[4]==2&&handshake[5]==1,"handshake payload");
    check(Protocol.summary(new byte[]{0,1,2},0).equals("HANDSHAKE_SLIDING_WINDOW_A"),"actual handshake");
    check(Protocol.handshakeResult(new byte[]{0,1,1}).equals("HANDSHAKE_LEGACY"),"legacy negotiated");
    check(Protocol.handshakeResult(new byte[]{0,1,2}).equals("HANDSHAKE_SLIDING_WINDOW_A"),"sliding A negotiated");
    check(Protocol.handshakeResult(new byte[]{0,1,3}).equals("HANDSHAKE_SLIDING_WINDOW_B"),"sliding B negotiated");
    check(Protocol.handshakeResult(new byte[]{0,(byte)255,0}).equals("HANDSHAKE_DEVICE_ERROR_FF"),"reject FF on handshake");
    check(Protocol.handshakeResult(new byte[]{0,1}).equals("MISSING_HANDSHAKE_REPLY"),"short handshake response");
    check(Protocol.handshakeResult(new byte[]{2,1,2}).equals("NOT_HANDSHAKE_REPLY"),"unexpected type");
    check(Protocol.handshakeResult(new byte[]{0,2,2}).startsWith("HANDSHAKE_UNKNOWN_STATUS"),"unexpected status");
    check(Protocol.handshakeResult(new byte[]{0,1,4}).startsWith("HANDSHAKE_UNKNOWN_MODE"),"unknown mode");
    check(Protocol.negotiatedTransport(Protocol.handshakeResult(new byte[]{0,1,1})),"Legacy accepted for transport-only test");
    check(Protocol.negotiatedTransport(Protocol.handshakeResult(new byte[]{0,1,2})),"Sliding A accepted for transport-only test");
    check(Protocol.negotiatedTransport(Protocol.handshakeResult(new byte[]{0,1,3})),"Sliding B accepted for transport-only test");
    check(!Protocol.negotiatedTransport(Protocol.handshakeResult(new byte[]{0,(byte)255,0})),"transport error never accepted");
    check(!Protocol.negotiatedTransport(Protocol.handshakeResult(new byte[]{0,1,4})),"unknown negotiated mode never accepted");
    check(Protocol.handshake().length==64,"handshake padded length");
    check(Protocol.hex(Protocol.handshake()).startsWith("00 00 03 03 02 01"),"original 321 preserved");
    check(Protocol.hex(Protocol.handshakeVariant(1)).startsWith("00 00 03 02 01 03"),"documented 213 preference");
    check(Protocol.hex(Protocol.handshakeVariant(2)).startsWith("00 00 03 01 02 03"),"documented 123 preference");
    boolean invalidPattern=false;try{Protocol.handshakeVariant(3);}catch(IllegalArgumentException expected){invalidPattern=true;}
    check(invalidPattern,"unknown handshake preference fails closed");
    byte[] rejection=new byte[64];rejection[1]=(byte)255;
    check(Protocol.parse(rejection,0x22,0x0220).status.equals("TRANSPORT_00_FF_UNDECODED"),"real 00FF");
    check(Protocol.summary(rejection,0x0220).equals("TRANSPORT_REJECTED_00_FF"),"compat status");
    check(Protocol.testerSummary(rejection).equals("TRANSPORT_REJECTED_00_FF"),"tester error");
    check(Protocol.transportSummary(rejection).equals("TRANSPORT_ERROR_00_FF"),"transport summary");

    byte[] did=Protocol.read(0x3A,0x0220,0);
    check(did.length==64,"request padded to 64");
    check(Protocol.hex(did).startsWith("02 00 07 00 FF FF 0B 00 00 00 3A 04 03 22 02 20"),"published frame");
    byte[] tester=Protocol.testerPresent(0x39,0);
    check(Protocol.hex(tester).startsWith("02 00 07 00 FF FF 0A 00 00 00 39 03 02 3E 00"),"tester candidate frame");
    check(Protocol.initial(0)[6]==7,"empty frame has no inner payload");
    byte[] reference=Protocol.publishedBatteryRequest();
    byte[] publishedBytes=new byte[]{2,0,7,0,(byte)255,(byte)255,11,0,0,0,0x3C,4,3,0x22,(byte)0xF1,0x30};
    check(java.util.Arrays.equals(java.util.Arrays.copyOf(reference,16),publishedBytes),
      "exact documented BMS request (no empty frame / TesterPresent)");
    check(Protocol.DOCUMENTED_BATTERY_CID==0x3C&&Protocol.DOCUMENTED_BATTERY_DID==0xF130,
      "documented reference target");

    byte[] pos=frame(11,0x3D,4,3,0x62);
    pos[14]=2;pos[15]=0x20;
    check(Protocol.parse(pos,0x22,0x0220).status.equals("UDS_DID_MATCH_FRAME_CANDIDATE"),"positive frame candidate");
    check(Protocol.summary(pos,0x0220).equals("UDS_POSITIVE_RAW_UNVERIFIED"),"compat positive");
    check(Protocol.parse(pos,0x22,0x0221).status.equals("UDS_DID_MISMATCH"),"mismatched DID");

    byte[] testerPos=frame(10,0x3D,3,2,0x7E);testerPos[14]=0;
    check(Protocol.testerSummary(testerPos).equals("TESTER_PRESENT_CANDIDATE_UNVERIFIED"),"tester framed reply");

    byte[] neg=frame(11,0x3D,4,3,0x7F);neg[14]=0x22;neg[15]=0x31;
    check(Protocol.parse(neg,0x22,0x0220).status.equals("UDS_NEGATIVE_0x31"),"UDS NRC");
    byte[] spoof=new byte[64];spoof[1]=0x62;spoof[2]=2;spoof[3]=0x20;
    check(!Protocol.parse(spoof,0x22,0x0220).framed(),"no header scanning false positives");
    byte[] corrupt=pos.clone();corrupt[4]=0;
    check(!Protocol.parse(corrupt,0x22,0x0220).framed(),"reject bad header");
    corrupt=pos.clone();corrupt[6]=2;
    check(!Protocol.parse(corrupt,0x22,0x0220).framed(),"reject bad length");
    corrupt=pos.clone();corrupt[12]=2;
    check(!Protocol.parse(corrupt,0x22,0x0220).framed(),"reject wrong ISO TP payload length");
    check(Protocol.parse(new byte[]{2,0,7},0x22,0).status.equals("SHORT_HMI_FRAME"),"short frame");
    check(!Protocol.parse(Protocol.initial(0),0x22,0).framed(),"empty cannot prove diagnostic channel");

    Protocol.Session session=new Protocol.Session();
    boolean notReady=false;try{session.readDid(0x3A,0x0220);}catch(IllegalStateException expected){notReady=true;}
    check(notReady,"no data before handshake");
    check(session.acceptHandshake(new byte[]{0,1,2}),"session handshake");
    check((session.empty()[1]&255)==0,"empty seq 0");
    check((session.tester(0x3A)[1]&255)==1,"tester seq 1");
    check((session.readDid(0x3A,0x0220)[1]&255)==2,"read seq 2");
    check(session.nextSequence()==3,"sequence state");
    session.checked(rejection,0x22,0x0220);
    check(session.failed(),"transport failure blocks session");
    boolean blocked=false;try{session.tester(0x3A);}catch(IllegalStateException expected){blocked=true;}
    check(blocked,"no blind retry after rejection");
    byte[] report=new byte[]{0x05,1,0x09,2,(byte)0x85,3,0x75,8,(byte)0x95,64,(byte)0x81,2};
    check(Protocol.hidReportIds(report).equals("0x03"),"HID report ID parsing");
    check(Protocol.hidReportIds(new byte[]{0x09,(byte)0x85}).equals("none"),
      "HID byte in usage data is not a Report ID");
    check(Protocol.hidReportIds(new byte[]{(byte)0xFE,2,0,(byte)0x85,2, (byte)0x85,7})
      .equals("0x07"),"HID long item skip");
    check(Protocol.hidReportIds(new byte[]{(byte)0x85}).equals("none"),
      "HID truncated item fails closed");
    check(Protocol.hidReportIds(null).equals("none"),
      "HID null descriptor");
    check(Protocol.DISCOVERY_CIDS.length==7,"community list remains research only");
    System.out.println(assertions+" Bosch BES2 pure-Java protocol assertions PASS");
  }
}
