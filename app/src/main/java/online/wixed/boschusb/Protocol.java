package online.wixed.boschusb;
import java.util.Arrays;
import java.util.Locale;

/** Bosch BES2 Intuvia HID research framing. Requests are read-only; TesterPresent is experimental. */
public final class Protocol {
  private Protocol(){}
  public static final int VID=0x108C, PID=0x0155, LEN=64;
  /** Literal published battery part-number probe (legacy CID may no longer be routed). */
  public static final int DOCUMENTED_BATTERY_CID=0x3C, DOCUMENTED_BATTERY_DID=0xF130;
  public static final int[] BUI={0x0220,0x0221,0x0222,0x0223,0x0224,0x0225,0x0226,0x0227,0x0228,0x0230,0x0231,0x0240,0x0241,0x0242,0x0243,0x0245,0x0246,0x0247,0x0248,0x0249,0x024A,0x024B,0x024C,0x024D,0x0250,0x0251,0x0264,0x0282,0x0283,0x0284,0x0285};
  public static final int[] DU={0xF130,0xF131,0xF133,0xF134,0xF139,0xF150,0xF151,0xF180};
  public static final int[] BMS={0xF130,0xF131,0xF132,0xF133,0xF134,0xF150,0xF151,0xF180};
  public static final String[] PROFILES={
   "Display BUI · CID 3A (observed)",
   "Display BUI · CID 39 (observed)",
   "Display BUI · CID 5D (observed)",
   "Motor DU · CID 3B (legacy, unverified)",
   "Battery BMS · CID 3C (legacy, unverified)"
  };
  public static int cid(int profile){return new int[]{0x3A,0x39,0x5D,0x3B,0x3C}[profile];}
  public static int[] dids(int profile){return profile<3?BUI:profile==3?DU:BMS;}
  public static String label(int did){
    switch(did){
    case 0x0220: return "Display software version";
    case 0x0221: return "Display hardware version";
    case 0x0222: return "Manufacture date";
    case 0x0242: return "Serial number (private)";
    case 0x0245: return "MaxSpeed (trip, NOT motor cut-off)";
    case 0x0248: return "PauseSpeedLimit (not motor cut-off)";
    case 0x0249: return "Last odometer";
    case 0x024A: return "Language";
    case 0x024B: return "Distance units";
    case 0x024C: return "Day brightness";
    case 0x0250: return "Night brightness";
    case 0x0251: return "Display contrast";
    case 0xF130: return "Part number";
    case 0xF131: return "Manufacture date";
    case 0xF132: return "BMS information / DU sensor";
    case 0xF133: return "Drive language / BMS lifetime";
    case 0xF134: return "Component configuration";
    case 0xF139: return "Bike configuration";
    case 0xF150: return "Hardware version";
    case 0xF151: return "Software version";
    default: return "Published DID (raw)";
    }
  }
  public static byte[] handshake(){byte[] a=new byte[LEN];a[2]=3;a[3]=3;a[4]=2;a[5]=1;return a;}
  public static byte[] initial(int seq){byte[] a=new byte[LEN];a[0]=2;a[1]=(byte)seq;a[2]=7;a[4]=(byte)255;a[5]=(byte)255;a[6]=7;return a;}
  public static byte[] read(int cid,int did,int seq){
    if(cid<0||cid>255||did<0||did>65535)throw new IllegalArgumentException("Invalid CID/DID");
    byte[] a=new byte[LEN];a[0]=2;a[1]=(byte)seq;a[2]=7;a[4]=(byte)255;a[5]=(byte)255;a[6]=11;
    a[10]=(byte)cid;a[11]=4;a[12]=3;a[13]=0x22;a[14]=(byte)(did>>8);a[15]=(byte)did;return a;
  }
  /** The exact single-frame request published in Bosch eBike HIM: 02 00 07 00 FF FF 0B 00 00 00 3C 04 03 22 F1 30.
   * It does not establish that this old address is alive on the attached bike. */
  public static byte[] publishedBatteryRequest(){return read(DOCUMENTED_BATTERY_CID,DOCUMENTED_BATTERY_DID,0);}
  public static String hex(byte[] bytes){StringBuilder s=new StringBuilder();for(byte b:bytes){if(s.length()>0)s.append(' ');s.append(String.format(Locale.US,"%02X",b&255));}return s.toString();}

  /** Parsed strictly using the published 64-byte HMI envelope + ISO-TP single frame.
   * Nothing is decoded from headers, unrelated offsets or unused zero padding.
   */
  public static final class Reply {
    public final String status;
    public final int responseCid;
    public final int sequence;
    public final int service;
    public final int did;
    Reply(String status,int cid,int sequence,int service,int did){
      this.status=status;this.responseCid=cid;this.sequence=sequence;this.service=service;this.did=did;
    }
    public boolean framed(){return responseCid>=0;}
  }
  private static int u(byte b){return b&255;}
  public static Reply parse(byte[] b,int requestedService,int expectedDid){
    if(b==null||b.length==0)return new Reply("NO_RESPONSE",-1,-1,-1,-1);
    if(b.length>=2&&u(b[0])==0&&u(b[1])==255)
      return new Reply("TRANSPORT_00_FF_UNDECODED",-1,-1,-1,-1);
    if(b.length>=3&&u(b[0])==0&&u(b[1])==1&&u(b[2])==2)
      return new Reply("HANDSHAKE_SLIDING_WINDOW_A",-1,-1,-1,-1);
    if(u(b[0])!=2)return new Reply("UNKNOWN_USB_FRAME",-1,-1,-1,-1);
    if(b.length<14)return new Reply("SHORT_HMI_FRAME",-1,-1,-1,-1);
    if(u(b[2])!=7||u(b[4])!=255||u(b[5])!=255)
      return new Reply("HMI_HEADER_NOT_VALIDATED",-1,-1,-1,-1);
    int declared=u(b[6]);
    if(declared<7||declared>LEN-7||7+declared>b.length)
      return new Reply("HMI_LENGTH_INVALID",-1,-1,-1,-1);
    if(declared==7)return new Reply("HMI_EMPTY_PACKET_UNVERIFIED",-1,u(b[1]),-1,-1);
    int cid=u(b[10]),messageLength=u(b[11]),isotpLength=u(b[12]);
    if(messageLength<2||messageLength>declared-7||isotpLength!=messageLength-1)
      return new Reply("ISO_TP_LENGTH_UNVERIFIED",-1,-1,-1,-1);
    if(isotpLength>=8||13+isotpLength>b.length)
      return new Reply("ISO_TP_SEGMENTED_UNSUPPORTED",-1,-1,-1,-1);
    int sid=u(b[13]),seq=u(b[1]);
    if(sid==0x7F&&isotpLength>=3&&u(b[14])==requestedService)
      return new Reply("UDS_NEGATIVE_0x"+String.format(Locale.US,"%02X",u(b[15])),cid,seq,sid,-1);
    if(requestedService==0x3E&&sid==0x7E&&isotpLength>=2&&u(b[14])==0)
      return new Reply("UDS_TESTER_POSITIVE_FRAME_CANDIDATE",cid,seq,sid,-1);
    if(requestedService==0x22&&sid==0x62&&isotpLength>=3){
      int did=(u(b[14])<<8)|u(b[15]);
      if(did==expectedDid)
        return new Reply("UDS_DID_MATCH_FRAME_CANDIDATE",cid,seq,sid,did);
      return new Reply("UDS_DID_MISMATCH",cid,seq,sid,did);
    }
    return new Reply("FRAMED_UNKNOWN_SERVICE",cid,seq,sid,-1);
  }
  public static String summary(byte[] b,int did){
    String status=parse(b,0x22,did).status;
    if(status.equals("TRANSPORT_00_FF_UNDECODED"))return "TRANSPORT_REJECTED_00_FF";
    if(status.equals("UDS_DID_MATCH_FRAME_CANDIDATE"))return "UDS_POSITIVE_RAW_UNVERIFIED";
    return status;
  }
  /** Bounded, fail-closed session tracker; an accepted handshake does not prove
   * a functioning Sliding Window A implementation or a routed ECU session. */
  public static final class Session {
    private boolean handshakeOk;
    private boolean failed;
    private int sequence;
    public boolean acceptHandshake(byte[] rx){
      handshakeOk="HANDSHAKE_SLIDING_WINDOW_A".equals(parse(rx,0,0).status);
      failed=!handshakeOk;sequence=0;return handshakeOk;
    }
    private void guard(){
      if(!handshakeOk||failed)throw new IllegalStateException("Transport not ready or rejected");
    }
    public byte[] empty(){guard();return initial(sequence++);}
    public byte[] tester(int cid){guard();return testerPresent(cid,sequence++);}
    public byte[] readDid(int cid,int did){guard();return read(cid,did,sequence++);}
    public Reply checked(byte[] rx,int service,int did){
      guard();Reply r=parse(rx,service,did);
      if(!r.framed())failed=true;
      return r;
    }
    public int nextSequence(){return sequence;}
    public boolean failed(){return failed;}
  }
  /**
   * Bounded discovery candidates reported by community heartbeat experiments.
   * These are NOT verified write channels, keys or brute-force security values.
   */
  public static final int[] DISCOVERY_CIDS={0x20,0x22,0x39,0x3A,0x5D,0x5E,0x5F};
  public static final int MAX_TRANSPORT_REJECTS=3;
  public static final long MIN_PROBE_DELAY_MS=1200;

  /**
   * UDS TesterPresent, service 0x3E subfunction 0x00.
   * Diagnostic keepalive (not a programming or configuration write).
   * It is NOT known to be identical to the unpublished Bosch heartbeat request.
   */
  public static byte[] testerPresent(int cid,int seq){
    if(cid<0||cid>255)throw new IllegalArgumentException("Bad CID");
    byte[] a=new byte[LEN]; a[0]=2; a[1]=(byte)seq; a[2]=7;
    a[4]=(byte)255; a[5]=(byte)255; a[6]=10;
    a[10]=(byte)cid;a[11]=3;a[12]=2;a[13]=0x3E;a[14]=0;
    return a;
  }

  /** Transport classification is separate from any guessed UDS payload. */
  public static String transportSummary(byte[] b) {
    if(b==null||b.length==0)return "NO_USB_REPLY";
    if(b.length>=2&&b[0]==0&&(b[1]&255)==255)return "TRANSPORT_ERROR_00_FF";
    if(b.length>=3&&b[0]==0&&b[1]==1&&b[2]==2)return "HANDSHAKE_ACCEPTED_A";
    if((b[0]&255)==2)return "HID_DATA_FRAME_UNVERIFIED";
    return "UNKNOWN_TRANSPORT_FRAME";
  }
  public static String testerSummary(byte[] b) {
    Reply reply=parse(b,0x3E,0);
    if(reply.status.equals("TRANSPORT_00_FF_UNDECODED"))return "TRANSPORT_REJECTED_00_FF";
    if(reply.status.equals("UDS_TESTER_POSITIVE_FRAME_CANDIDATE"))return "TESTER_PRESENT_CANDIDATE_UNVERIFIED";
    return reply.status;
  }
  public static String didHex(int did){return String.format(Locale.US,"%04X",did);}
}
