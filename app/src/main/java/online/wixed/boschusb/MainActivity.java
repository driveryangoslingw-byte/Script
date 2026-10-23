package online.wixed.boschusb;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.hardware.usb.*;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.View;
import android.view.ViewGroup;
import android.widget.*;
import java.io.OutputStream;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import org.json.*;

/** Explicit read-only diagnostic transport for Bosch BES2 HID 108C:0155. */
public final class MainActivity extends Activity {
  private static final String USB_ACTION="online.wixed.boschdiag.USB_PERMISSION";
  private static final int EXPORT_REQUEST=318;
  private final Handler ui=new Handler(Looper.getMainLooper());
  private final ExecutorService worker=Executors.newSingleThreadExecutor();
  private final StringBuilder log=new StringBuilder();
  private final ArrayList<JSONObject> packetLog=new ArrayList<>();
  private final ArrayList<JSONObject> values=new ArrayList<>();
  private UsbManager manager;
  private volatile UsbDevice device;
  private UsbDeviceConnection conn;
  private UsbInterface iface;
  private UsbEndpoint in,out;
  private TextView stateText, progressText, traceText, resultText;
  private CheckBox initialCheck;
  private Spinner profileSpinner,handshakeSpinner;
  private volatile boolean busy=false, cancel=false, pendingAuto=false;
  private String exportData="";
  private int count=0;
  private final BroadcastReceiver usbReceiver=new BroadcastReceiver(){
    @Override public void onReceive(Context context,Intent intent){
      String action=intent.getAction();
      if(USB_ACTION.equals(action)){
        UsbDevice d=deviceFrom(intent);
        boolean callback=intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED,false);
        boolean real=device!=null&&manager.hasPermission(device);
        post("USB permission callback="+callback+", actual="+real);
        if(d!=null && (d.getVendorId()!=Protocol.VID||d.getProductId()!=Protocol.PID))return;
        if(real)worker.execute(()->{open(device);afterUsbOpened();});
        else {pendingAuto=false;updateState("USB access denied. Select Allow on Android USB dialog.",false);}
      }else if(UsbManager.ACTION_USB_DEVICE_DETACHED.equals(action)){
        UsbDevice d=deviceFrom(intent);
        if(d!=null&&d.getVendorId()==Protocol.VID&&d.getProductId()==Protocol.PID){
          cancel=true;pendingAuto=false;
          updateState("Bosch USB disconnected",false);
          worker.execute(()->closeConnection());
        }
      }
    }
  };
  private UsbDevice deviceFrom(Intent i){
    if(Build.VERSION.SDK_INT>=33)return i.getParcelableExtra(UsbManager.EXTRA_DEVICE,UsbDevice.class);
    return (UsbDevice)i.getParcelableExtra(UsbManager.EXTRA_DEVICE);
  }
  @Override public void onCreate(Bundle state){
    super.onCreate(state);
    manager=(UsbManager)getSystemService(USB_SERVICE);
    IntentFilter f=new IntentFilter(USB_ACTION);
    f.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
    if(Build.VERSION.SDK_INT>=33)registerReceiver(usbReceiver,f,Context.RECEIVER_EXPORTED);
    else registerReceiver(usbReceiver,f);
    buildUI();
    post("BES2 4.4: one-tap evidence collection, current BUI route and early stop.");
    post("This is not a speed-unlock tool; device-specific read responses remain unverified.");
  }
  private TextView text(String message,int size,int color){
    TextView v=new TextView(this);v.setText(message);v.setTextColor(color);
    v.setTextSize(size);v.setPadding(7,10,7,10);return v;
  }
  private void add(LinearLayout l,View v){l.addView(v,new LinearLayout.LayoutParams(-1,-2));}
  private void button(LinearLayout l,String name,Runnable action){
    Button b=new Button(this);b.setAllCaps(false);b.setText(name);b.setTextColor(0xff142332);
    b.setOnClickListener(v->action.run());add(l,b);
  }
  private void buildUI(){
    ScrollView scroll=new ScrollView(this);
    LinearLayout root=new LinearLayout(this);
    root.setOrientation(LinearLayout.VERTICAL);
    root.setBackgroundColor(0xff0b1424);
    root.setPadding(22,18,22,38);
    scroll.addView(root);
    TextView head=text("BOSCH BES2  •  USB INVESTIGATION 4.4",22,0xff87dcff);
    head.setTypeface(null,Typeface.BOLD);add(root,head);
    add(root,text("Intuvia / BES2 · USB 108C:0155 · offline, read-only",14,0xffacc0d8));
    stateText=text("Connect the stationary, powered-on bike via a USB data cable.",16,0xffffc878);
    add(root,stateText);
    button(root,"▶  START AUTOMATIC DIAGNOSIS",this::oneTap);
    add(root,text("One button inspects USB HID, negotiates the observed 321 transport, captures 3 seconds of incoming traffic, then sends one read-only BUI query (CID 3A / DID 0220). Stops on 00 FF. No unsupported CID scans.",13,0xffd3e7fb));
    button(root,"■  STOP",()->{cancel=true;pendingAuto=false;post("Stop requested.");});
    progressText=text("Ready · no ECU readings confirmed",15,0xffc9ddf6);add(root,progressText);
    resultText=text("Automatic results will appear here.",14,0xffd5f5cd);
    resultText.setTextIsSelectable(true);add(root,resultText);
    button(root,"Save diagnostic JSON to a file",this::saveReport);
    button(root,"Copy diagnostic JSON",this::copyReport);
    button(root,"Clear screen / results",this::clearScreen);
    add(root,text("RAW USB TRACE  ·  May contain serial numbers: review before sharing.",12,0xffffd08b));
    traceText=text("",12,0xffa9c7ea);traceText.setTypeface(Typeface.MONOSPACE);
    traceText.setTextIsSelectable(true);add(root,traceText);
    add(root,text("Transport protocol remains partially undocumented. No security access, programming, parameter writes, firmware or speed-limit modifications.",12,0xffa6b9cf));
    setContentView(scroll);
  }
  private void post(String message){
    synchronized(log){log.append(new Date()).append("  ").append(message).append('\n');}
    ui.post(()->{
      if(traceText!=null){
        String s; synchronized(log){s=log.toString();}
        traceText.setText(s.length()>27000?s.substring(s.length()-27000):s);
      }
    });
  }
  private void updateState(String s,boolean ok){ui.post(()->{
    if(stateText!=null){stateText.setText(s);stateText.setTextColor(ok?0xffaaffaa:0xffffc47e);}
  });post(s);}
  private void connect(){
    if(busy){post("Busy");return;}
    UsbDevice selected=null;
    for(UsbDevice d:manager.getDeviceList().values()){
      if(d.getVendorId()==Protocol.VID&&d.getProductId()==Protocol.PID){selected=d;break;}
    }
    if(selected==null){pendingAuto=false;updateState("Bosch 108C:0155 not found. Check USB OTG and cable.",false);return;}
    device=selected;post("Detected USB device "+device.getDeviceName()+" with "+device.getInterfaceCount()+" interface(s).");
    if(manager.hasPermission(device)){worker.execute(()->{open(device);afterUsbOpened();});return;}
    Intent intent=new Intent(USB_ACTION).setPackage(getPackageName());
    int flags=PendingIntent.FLAG_UPDATE_CURRENT;
    if(Build.VERSION.SDK_INT>=31)flags|=PendingIntent.FLAG_MUTABLE;
    PendingIntent pending=PendingIntent.getBroadcast(this,0,intent,flags);
    manager.requestPermission(device,pending);
    updateState("Accept the Android USB permission prompt",false);
  }
  private synchronized void open(UsbDevice target){
    closeConnection();
    try{
      if(target==null||!manager.hasPermission(target))throw new Exception("USB permission unavailable");
      for(int i=0;i<target.getInterfaceCount();i++){
        UsbInterface f=target.getInterface(i);
        UsbEndpoint read=null,write=null;
        for(int j=0;j<f.getEndpointCount();j++){
          UsbEndpoint ep=f.getEndpoint(j);
          if(ep.getType()!=UsbConstants.USB_ENDPOINT_XFER_INT)continue;
          if(ep.getDirection()==UsbConstants.USB_DIR_IN)read=ep;
          else if(ep.getDirection()==UsbConstants.USB_DIR_OUT)write=ep;
        }
        if(read==null)continue;
        UsbDeviceConnection c=manager.openDevice(target);
        if(c==null)throw new Exception("openDevice failed");
        if(!c.claimInterface(f,true)){c.close();continue;}
        conn=c;iface=f;in=read;out=write;device=target;
        updateState("Bosch HID connected. IN ep="+read.getAddress()+", OUT="+(write==null?"HID control":write.getAddress()),true);
        post("USB interface="+f.getId()+" class="+f.getInterfaceClass()+" subclass="+f.getInterfaceSubclass()+
          " protocol="+f.getInterfaceProtocol()+"; IN maxPacket="+read.getMaxPacketSize()+
          "; OUT maxPacket="+(write==null?"control":write.getMaxPacketSize()));
        return;
      }
      throw new Exception("No interrupt IN endpoint found");
    }catch(Exception e){updateState("Open failed: "+e.getMessage(),false);closeConnection();}
  }
  private synchronized void closeConnection(){
    if(conn!=null){
      try{if(iface!=null)conn.releaseInterface(iface);}catch(Exception ignored){}
      try{conn.close();}catch(Exception ignored){}
    }
    conn=null;iface=null;in=null;out=null;
  }
  private void record(String direction,byte[] bytes,String detail){
    final String h=Protocol.hex(bytes);
    post(direction+" "+detail+"\n"+h);
    JSONObject row=new JSONObject();
    try{row.put("time_ms",System.currentTimeMillis());row.put("direction",direction);row.put("detail",detail);row.put("hex",h);}catch(Exception ignored){}
    synchronized(packetLog){packetLog.add(row);}
  }
  private byte[] exchange(byte[] bytes,String label)throws Exception{
    if(conn==null||in==null||iface==null)throw new Exception("USB not connected");
    if(cancel)throw new Exception("Stopped by user");
    record("OUT",bytes,label);
    int sent=out!=null
      ?conn.bulkTransfer(out,bytes,Protocol.LEN,1600)
      :conn.controlTransfer(0x21,0x09,0x0200,iface.getId(),bytes,Protocol.LEN,1600);
    if(sent!=Protocol.LEN)throw new Exception("USB OUT failed: "+sent);
    byte[] receive=new byte[Protocol.LEN];
    int n=conn.bulkTransfer(in,receive,Protocol.LEN,1900);
    if(n<=0)throw new Exception("USB timeout / IN error: "+n);
    byte[] reply=Arrays.copyOf(receive,n);record("IN",reply,label);
    post("TRANSPORT "+Protocol.transportSummary(reply)+"; UDS decoding is separate and may be unverified.");
    return reply;
  }
  private void startJob(Runnable op){
    if(busy){post("Another diagnostic operation is running");return;}
    busy=true;cancel=false;worker.execute(()->{
      try{op.run();}finally{busy=false;}
    });
  }
  /** One user action, including USB permission continuation. */
  private void oneTap(){
    if(busy||pendingAuto){post("Automatic diagnosis is already running or awaiting USB permission.");return;}
    pendingAuto=true;cancel=false;
    updateState("Starting automatic USB connection...",true);
    connect();
  }
  private void afterUsbOpened(){
    if(!pendingAuto)return;
    pendingAuto=false;
    if(conn==null||in==null){updateState("Automatic diagnosis cannot open the Bosch USB interface.",false);return;}
    startJob(this::automaticFlow);
  }
  private void autoResult(String label,String status,String raw){
    JSONObject row=new JSONObject();
    try{row.put("mode","AUTO_ONE_TAP");row.put("step",label);row.put("status",status);row.put("raw",raw);}
    catch(Exception ignored){}
    synchronized(values){values.add(row);}
    final String line=label+" → "+status;
    post("AUTO RESULT "+line);
    ui.post(()->resultText.append(line+"\n"));
  }
  private void ensureNotStopped()throws Exception{
    if(cancel)throw new Exception("Stopped by user");
    if(device==null||conn==null||in==null)throw new Exception("USB is disconnected");
  }
  private void reopenForAuto()throws Exception{
    ensureNotStopped();
    UsbDevice target=device;
    closeConnection();
    open(target);  // software USB-interface reopen is NOT a bicycle power cycle
    ensureNotStopped();
  }
  /**
   * One-touch evidence collection. Source-grounded 321 handshake only:
   * repeated preference negotiations were already measured on this bike and
   * do not add ECU access. The 3C BMS CID is an obsolete reference target,
   * therefore a current observed BUI target is used for the ONE bounded read.
   * 00 FF is still an undecoded device transport error, never an ECU value.
   */
  private void automaticFlow(){
    String outcome="Incomplete";
    try{
      post("AUTO BEGIN 4.4: USB HID descriptor -> 321 handshake -> passive capture -> one BUI read.");
      post("Bike powered on with Intuvia installed (user confirmed). Legacy BMS CID 3C removed from auto probe.");
      post("This does not implement proprietary Bosch heartbeat / Sliding Window A internals.");
      ui.post(()->{resultText.setText("");progressText.setText("Working · HID descriptor");});
      ensureNotStopped();
      byte[] descriptor=new byte[512];
      int n=conn.controlTransfer(0x81,0x06,0x2200,iface.getId(),descriptor,descriptor.length,1800);
      if(n>0){
        byte[] raw=Arrays.copyOf(descriptor,n);
        record("USB_DESCRIPTOR",raw,"AUTO HID report descriptor (not an ECU reply)");
        autoResult("HID descriptor",n+" bytes; Report IDs="+Protocol.hidReportIds(raw),Protocol.hex(raw));
      }else autoResult("HID descriptor","Unavailable ("+n+"); continue with negotiated transport","");
      ensureNotStopped();
      ui.post(()->progressText.setText("Working · 321 handshake"));
      byte[] handshake=exchange(Protocol.handshake(),"AUTO single documented handshake 321");
      String mode=Protocol.handshakeResult(handshake);
      autoResult("Handshake 321",mode,Protocol.hex(handshake));
      if(!"HANDSHAKE_SLIDING_WINDOW_A".equals(mode))
        throw new Exception("321 negotiation was not Sliding Window A: "+mode+"; no ECU payload sent.");
      post("HANDSHAKE OK: USB transport only. CAN routing and ECU diagnostic session remain UNVERIFIED.");

      // Capture a longer quiet period to distinguish unsolicited input
      // from the prior 450ms receive-timeout observation. No USB output here.
      ui.post(()->progressText.setText("Working · 3s passive capture (no outgoing packets)"));
      int passiveCount=0;
      for(int i=0;i<4;i++){
        ensureNotStopped();
        byte[] input=new byte[Protocol.LEN];
        long captureStart=android.os.SystemClock.elapsedRealtime();
        int bytes=conn.bulkTransfer(in,input,Protocol.LEN,750);
        if(bytes>0){
          byte[] raw=Arrays.copyOf(input,bytes);
          record("IN",raw,"AUTO unsolicited packet "+(++passiveCount));
          autoResult("Passive receive "+(i+1),Protocol.transportSummary(raw),Protocol.hex(raw));
        }else post("AUTO passive receive "+(i+1)+"/4: "+(bytes==-1?"no packet":"USB returned "+bytes)+
          " after "+(android.os.SystemClock.elapsedRealtime()-captureStart)+"ms");
      }
      autoResult("Passive 3s",passiveCount+" unsolicited IN packets","");

      ensureNotStopped();
      ui.post(()->progressText.setText("Working · one BUI read, then stop"));
      // This target was actually observed in the BES2 research's current CID
      // table, unlike the legacy BMS CID 3C used in app version 4.3.
      byte[] request=Protocol.read(Protocol.OBSERVED_BUI_CID,Protocol.OBSERVED_BUI_DID,0);
      byte[] reply=exchange(request,"AUTO observed BUI CID 3A / DID 0220 (one attempt)");
      Protocol.Reply decoded=Protocol.parse(reply,0x22,Protocol.OBSERVED_BUI_DID);
      autoResult("BUI 3A / DID 0220",decoded.status,Protocol.hex(reply));
      if("TRANSPORT_00_FF_UNDECODED".equals(decoded.status)){
        outcome="USB works; A handshake succeeds. BUI read returned 00 FF again. "+
          "Reason not decoded: framing, route, or missing Bosch-specific session. "+
          "Need a captured working BES2 diagnostic exchange to fix the protocol.";
        updateState(outcome,false);
      }else if("UDS_DID_MATCH_FRAME_CANDIDATE".equals(decoded.status)){
        outcome="BUI raw reply candidate received; framing and ECU origin not validated.";
        updateState(outcome,false);
      }else{
        outcome="BUI reply "+decoded.status+"; no verified ECU value. Transport investigation required.";
        updateState(outcome,false);
      }
    }catch(Exception e){
      outcome="AUTO STOP: "+e.getMessage();
      updateState(outcome,false);
    }finally{
      final String result=outcome;
      ui.post(()->progressText.setText("Finished · "+result+" · 0 verified ECU values"));
      try(FileOutputStream output=openFileOutput("bosch-bes2-last-diagnostic.json",MODE_PRIVATE)){
        output.write(buildReport().getBytes(StandardCharsets.UTF_8));
        post("Report saved inside app: bosch-bes2-last-diagnostic.json");
      }catch(Exception e){post("Internal report save failed: "+e.getMessage());}
    }
  }

  private void runProbe(){startJob(()->{
    try{
      byte[] r=exchange(Protocol.handshake(),"Bosch handshake");
      String code=Protocol.summary(r,0);
      if(!code.equals("HANDSHAKE_SLIDING_WINDOW_A"))throw new Exception("Unexpected handshake: "+code);
      updateState("USB handshake confirmed: sliding window A",true);
    }catch(Exception e){updateState("Handshake error: "+e.getMessage(),false);}
  });}

  /**
   * Compare the three ordered protocol preferences published in Bosch eBike HIM.
   * Only a HID transport negotiation packet is sent. No CAN payload follows,
   * including when the device negotiates an unsupported transport mode.
   */
  private void probeHandshakePreference(){
    final int index=handshakeSpinner.getSelectedItemPosition();
    final String preference=Protocol.HANDSHAKE_PATTERNS[index];
    startJob(()->{
      try{
        post("HANDSHAKE_ONLY preference="+preference+
          " (three documented modes, NOT a CID, no data/UDS traffic).");
        byte[] rx=exchange(Protocol.handshakeVariant(index),
          "Preference "+preference+" / USB handshake only");
        String mode=Protocol.handshakeResult(rx);
        final String output="Handshake "+preference+" => "+mode+
          " / raw="+Protocol.hex(Arrays.copyOf(rx,Math.min(3,rx.length)));
        post(output);
        ui.post(()->resultText.append(output+"\\n"));
        updateState(output,"HANDSHAKE_SLIDING_WINDOW_A".equals(mode)||
          "HANDSHAKE_SLIDING_WINDOW_B".equals(mode)||
          "HANDSHAKE_LEGACY".equals(mode));
        post("No ECU requests sent. To test a different preference, re-connect the bike and select it manually.");
      }catch(Exception e){updateState("Handshake-only probe failed: "+e.getMessage(),false);}
    });
  }

  /**
   * Independent one-shot, source-grounded experiment. The HMI reference
   * explicitly allows starting without the first empty frame and includes
   * a direct 0x22 example without TesterPresent. Never try further requests
   * after a transport error or a negative UDS reply.
   */
  private void directRead(boolean published){
    final int profile=profileSpinner.getSelectedItemPosition();
    final int targetCid=published?Protocol.DOCUMENTED_BATTERY_CID:Protocol.cid(profile);
    final int did=published?Protocol.DOCUMENTED_BATTERY_DID:(profile<3?0x0220:0xF130);
    final String variant=published?"PUBLISHED_BMS_EXAMPLE":"DIRECT_SELECTED_COMPONENT";
    startJob(()->{
      Protocol.Session session=new Protocol.Session();
      String cid=String.format(Locale.US,"%02X",targetCid);
      try{
        post(variant+": handshake -> one DID only; no empty packet, no TesterPresent, no CID sweep.");
        byte[] h=exchange(Protocol.handshake(),variant+" / step 1 / handshake");
        if(!session.acceptHandshake(h))throw new Exception("Handshake not accepted: "+Protocol.transportSummary(h));
        post(variant+": handshake accepted; now sending only one read-only request.");
        byte[] request=published?Protocol.publishedBatteryRequest():session.readDid(targetCid,did);
        byte[] rx=exchange(request,variant+" / step 2 / CID "+cid+" / DID "+Protocol.didHex(did));
        Protocol.Reply result=session.checked(rx,0x22,did);
        final boolean candidate="UDS_DID_MATCH_FRAME_CANDIDATE".equals(result.status);
        final String resultLine=variant+" / CID "+cid+" / DID "+Protocol.didHex(did)+
          " -> "+result.status+(candidate?" [UNVERIFIED payload; response format needs validation]":"");
        JSONObject row=new JSONObject();
        row.put("mode",variant);row.put("cid",cid);row.put("did",Protocol.didHex(did));
        row.put("status",result.status);row.put("response_cid",result.responseCid);
        row.put("raw",Protocol.hex(rx));row.put("source_packet",published);
        synchronized(values){values.add(row);}
        post(resultLine);
        ui.post(()->{
          resultText.append(resultLine+"\n");
          progressText.setText(candidate?"DID response candidate (not validated)":"No confirmed DID response");
        });
        if("TRANSPORT_00_FF_UNDECODED".equals(result.status)){
          updateState("USB replied 00 FF. Exact transport error meaning unverified; no further traffic.",false);
        }else{
          updateState(resultLine,candidate);
        }
      }catch(Exception e){updateState(variant+" STOP: "+e.getMessage(),false);}
    });
  }

  /**
   * Read the standard USB HID Report Descriptor; this is a USB descriptor
   * request, not a diagnostic command to the bicycle's CAN network.
   * It can reveal whether report IDs are required by the interface.
   */
  private void inspectHid(){
    startJob(()->{
      try{
        if(conn==null||iface==null)throw new Exception("Connect USB first");
        byte[] descriptor=new byte[512];
        int n=conn.controlTransfer(0x81,0x06,0x2200,iface.getId(),
          descriptor,descriptor.length,1800);
        if(n<0){updateState("HID report descriptor request failed: "+n,false);return;}
        if(n==0){updateState("HID report descriptor is empty",false);return;}
        byte[] raw=Arrays.copyOf(descriptor,n);
        record("USB_DESCRIPTOR",raw,"HID Report Descriptor / "+n+" bytes (not ECU response)");
        String reportIds=Protocol.hidReportIds(raw);
        String details="HID report descriptor: "+n+" bytes; declared Report IDs="+
          reportIds+
          "; active USB interrupt endpoints "+(in==null?"none":in.getMaxPacketSize())+" bytes IN / "+
          (out==null?"control":out.getMaxPacketSize()+" bytes OUT");
        post(details);updateState(details,true);
        ui.post(()->resultText.append(details+"\n"));
      }catch(Exception e){updateState("HID descriptor inspection stopped: "+e.getMessage(),false);}
    });
  }

  /**
   * Fail-closed flow. We deliberately do not claim that UDS TesterPresent is
   * the Bosch-specific discovery heartbeat: that wire packet is not verified.
   * One CID per invocation; no blind CID sweep, writes or security requests.
   */
  private void guidedRead(){
    final int profile=profileSpinner.getSelectedItemPosition();
    final boolean sendEmpty=initialCheck.isChecked();
    startJob(()->{
      Protocol.Session session=new Protocol.Session();
      String cid=String.format(Locale.US,"%02X",Protocol.cid(profile));
      try{
        post("GUIDED BEGIN: candidate CID "+cid+"; empty frame="+sendEmpty+
          "; original Bosch heartbeat format UNKNOWN; no brute force.");
        byte[] handshake=exchange(Protocol.handshake(),"Guided step 1 / USB handshake");
        if(!session.acceptHandshake(handshake))
          throw new Exception("Step 1: unexpected handshake "+Protocol.transportSummary(handshake));
        updateState("Step 1 OK: Sliding Window A negotiated (routing unverified)",true);
        if(sendEmpty){
          byte[] rx=exchange(session.empty(),"Guided step 2 / published initial empty frame");
          Protocol.Reply reply=session.checked(rx,0,0);
          post("Initial frame response: "+reply.status+
            " / expected sequence next="+session.nextSequence());
          if(!reply.framed()){
            updateState("Step 2 STOP: "+reply.status+
              ". Empty-frame handling is unresolved; do not continue guessing.",false);
            return;
          }
          post("Initial frame resembles a Bosch HMI data envelope; semantics remain unverified.");
        }else{
          post("Step 2 skipped by user. Published reference says empty frame can be optional.");
        }
        byte[] rx=exchange(session.tester(Protocol.cid(profile)),
          "Guided step 3 / UDS TesterPresent candidate / CID "+cid);
        Protocol.Reply tester=session.checked(rx,0x3E,0);
        post("TesterPresent: "+tester.status+
          (tester.framed()?" response-CID="+String.format(Locale.US,"%02X",tester.responseCid):""));
        if(!"UDS_TESTER_POSITIVE_FRAME_CANDIDATE".equals(tester.status)){
          updateState("Step 3 STOP: "+tester.status+
            ". No verified response to this tester/heartbeat candidate.",false);
          return;
        }
        int did=profile<3?0x0220:0xF130;
        rx=exchange(session.readDid(Protocol.cid(profile),did),
          "Guided step 4 / one read-only DID "+Protocol.didHex(did)+" / CID "+cid);
        Protocol.Reply result=session.checked(rx,0x22,did);
        JSONObject record=new JSONObject();
        record.put("mode","guided_read_only");
        record.put("cid",cid);record.put("did",Protocol.didHex(did));
        record.put("response_cid",result.responseCid);
        record.put("state",result.status);
        record.put("raw",Protocol.hex(rx));
        synchronized(values){values.add(record);}
        final String resultLine="CID "+cid+", DID "+Protocol.didHex(did)+" → "+
          result.status+" (transport research candidate)";
        post(resultLine);
        ui.post(()->{resultText.append(resultLine+"\\n");
          progressText.setText("Guided probe complete · "+result.status);});
        updateState("Step 4: "+result.status+
          " (no ECU values confirmed until framing is validated)",result.framed());
      }catch(Exception e){updateState("GUIDED STOP: "+e.getMessage(),false);}
    });
  }

  /**
   * Capture any unsolicited USB IN traffic after handshake. Never sends
   * TesterPresent, UDS reads or other device-targeted packets.
   */
  private void passiveListen(){
    startJob(()->{
      try{
        byte[] h=exchange(Protocol.handshake(),"Passive mode / handshake only");
        if(!"HANDSHAKE_SLIDING_WINDOW_A".equals(Protocol.summary(h,0)))
          throw new Exception("Handshake not confirmed");
        post("PASSIVE: 4x 750ms receive windows; no outbound diagnostic payloads.");
        int found=0;
        for(int i=0;i<4&&!cancel;i++){
          if(conn==null||in==null)throw new Exception("USB disconnected");
          byte[] raw=new byte[Protocol.LEN];
          int n=conn.bulkTransfer(in,raw,Protocol.LEN,750);
          if(n>0){
            found++;
            byte[] rx=Arrays.copyOf(raw,n);
            record("IN",rx,"PASSIVE unsolicited packet #"+found);
            post("PASSIVE classification: "+Protocol.transportSummary(rx));
          }else post("PASSIVE receive window "+(i+1)+": no packet ("+n+")");
        }
        final int packets=found;
        ui.post(()->resultText.append("Passive mode: "+packets+
          " unsolicited packet(s). A timeout is not a transport rejection.\\n"));
        updateState("Passive capture complete: "+packets+" unsolicited USB IN frames.",true);
      }catch(Exception e){updateState("Passive capture stopped: "+e.getMessage(),false);}
    });
  }
  private void clearScreen(){
    if(busy){post("Stop diagnostics first");return;}
    synchronized(packetLog){packetLog.clear();}
    synchronized(values){values.clear();}
    synchronized(log){log.setLength(0);}
    ui.post(()->{traceText.setText("");resultText.setText("");progressText.setText("Read: 0 confirmed");});
  }
  private String buildReport(){
    JSONObject root=new JSONObject();
    try{
      root.put("app","Bosch BES2 USB Lab 4.4 (one-tap, experimental read-only)");
      root.put("usb_vid_pid","108C:0155");
      root.put("hardware_validated",false);root.put("heartbeat_protocol_verified",false);root.put("sliding_window_a_full_implementation_verified",false);
      root.put("note","The JSON trace may include device serial numbers. Do not publish unredacted logs.");
      synchronized(values){root.put("results",new JSONArray(values));}
      synchronized(packetLog){root.put("trace",new JSONArray(packetLog));}
      synchronized(log){root.put("text_log",log.toString());}
      return root.toString(2);
    }catch(Exception e){return "{\"error\":\"report generation failed\"}";}
  }
  private void copyReport(){
    ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Bosch diagnostics",buildReport()));
    Toast.makeText(this,"Report copied",Toast.LENGTH_SHORT).show();
  }
  private void saveReport(){
    exportData=buildReport();
    Intent i=new Intent(Intent.ACTION_CREATE_DOCUMENT);
    i.addCategory(Intent.CATEGORY_OPENABLE);
    i.setType("application/json");
    i.putExtra(Intent.EXTRA_TITLE,"bosch-bes2-diagnostics.json");
    startActivityForResult(i,EXPORT_REQUEST);
  }
  @Override protected void onActivityResult(int request,int result,Intent data){
    super.onActivityResult(request,result,data);
    if(request==EXPORT_REQUEST&&result==RESULT_OK&&data!=null&&data.getData()!=null){
      try(OutputStream output=getContentResolver().openOutputStream(data.getData())){
        if(output==null)throw new Exception("Unable to open output file");
        output.write(exportData.getBytes(StandardCharsets.UTF_8));post("Diagnostic JSON saved.");
      }catch(Exception e){post("Report save failed: "+e.getMessage());}
    }
  }
  @Override protected void onDestroy(){
    cancel=true;
    try{unregisterReceiver(usbReceiver);}catch(Exception ignored){}
    worker.execute(this::closeConnection);worker.shutdown();
    super.onDestroy();
  }
}
