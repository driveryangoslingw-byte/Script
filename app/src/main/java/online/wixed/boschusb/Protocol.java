package online.wixed.boschusb;
import java.util.Arrays;
import java.util.Locale;

/** Bosch BES2 Intuvia HID research framing. Only 0x22 read requests are constructed. */
public final class Protocol {
  private Protocol(){}
  public static final int VID=0x108C, PID=0x0155, LEN=64;
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
  public static String hex(byte[] bytes){StringBuilder s=new StringBuilder();for(byte b:bytes){if(s.length()>0)s.append(' ');s.append(String.format(Locale.US,"%02X",b&255));}return s.toString();}
  public static String summary(byte[] b,int did){
    if(b==null||b.length==0)return "NO RESPONSE";
    if(b.length>=2&&b[0]==0&&(b[1]&255)==255)return "TRANSPORT_REJECTED_00_FF";
    if(b.length>=3&&b[0]==0&&b[1]==1&&b[2]==2)return "HANDSHAKE_SLIDING_WINDOW_A";
    if(b.length<4)return "SHORT_RESPONSE";
    for(int i=1;i+3<=b.length;i++){
      if((b[i]&255)==0x7F&&(b[i+1]&255)==0x22)return "UDS_NEGATIVE_0x"+String.format(Locale.US,"%02X",b[i+2]&255);
      if((b[i]&255)==0x62&&(b[i+1]&255)==((did>>8)&255)&&(b[i+2]&255)==(did&255))
        return "UDS_POSITIVE_RAW_UNVERIFIED";
    }
    return "UNCLASSIFIED_RAW";
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
    if(b==null||b.length==0)return "NO_RESPONSE";
    if(b.length>=2&&b[0]==0&&(b[1]&255)==255)return "TRANSPORT_REJECTED_00_FF";
    if(b.length>=3&&b[0]==0&&b[1]==1&&b[2]==2)return "HANDSHAKE_ONLY";
    // Only look inside likely message body, not untrusted header/padding.
    for(int i=11;i+1<b.length;i++){
      if((b[i]&255)==0x7E&&(b[i+1]&255)==0x00)return "TESTER_PRESENT_CANDIDATE_UNVERIFIED";
      if(i+2<b.length&&(b[i]&255)==0x7F&&(b[i+1]&255)==0x3E)
        return "UDS_TESTER_NEGATIVE_0x"+String.format(Locale.US,"%02X",b[i+2]&255);
    }
    return "NO_VERIFIED_TESTER_REPLY";
  }
  public static String didHex(int did){return String.format(Locale.US,"%04X",did);}
}
