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
  private Spinner profileSpinner;
  private volatile boolean busy=false, cancel=false;
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
        if(real)worker.execute(()->open(device));
        else updateState("USB access denied. Select Allow on Android USB dialog.",false);
      }else if(UsbManager.ACTION_USB_DEVICE_DETACHED.equals(action)){
        UsbDevice d=deviceFrom(intent);
        if(d!=null&&d.getVendorId()==Protocol.VID&&d.getProductId()==Protocol.PID){
          cancel=true;
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
    post("BES2 4.1: documented direct read + guided transport diagnostics.");
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
    TextView head=text("BOSCH BES2  •  TRANSPORT LAB 4.1",22,0xff87dcff);
    head.setTypeface(null,Typeface.BOLD);add(root,head);
    add(root,text("Intuvia / E-Bike HMI 108C:0155 | FREE · OFFLINE",13,0xffacc0d8));
    stateText=text("Not connected",17,0xffffc878);add(root,stateText);
    button(root,"1 · Find Bosch / Connect",this::connect);
    button(root,"2 · Check USB handshake",()->runProbe());
    add(root,text("DIAGNOSTIC PROFILE (select the component / channel)",14,0xff87dcff));
    profileSpinner=new Spinner(this);
    ArrayAdapter<String> adapter=new ArrayAdapter<>(this,android.R.layout.simple_spinner_item,Protocol.PROFILES);
    adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
    profileSpinner.setAdapter(adapter);
    profileSpinner.setBackgroundColor(0xffc8d7ec);
    add(root,profileSpinner);
    initialCheck=new CheckBox(this);initialCheck.setText("Include first empty frame (published sequence)");
    initialCheck.setTextColor(0xffe4efff);initialCheck.setChecked(true);add(root,initialCheck);
    add(root,text("CID values are research candidates, not verified ECU addresses. The original Bosch heartbeat bytes are NOT publicly verified.",12,0xffffd08b));
    button(root,"3 · DIRECT read / selected CID (no heartbeat)",()->directRead(false));
    button(root,"4 · PUBLISHED BMS example (CID 3C, DID F130)",()->directRead(true));
    button(root,"5 · Guided handshake / tester / DID",this::guidedRead);
    button(root,"6 · Passive USB receive (no CID traffic)",this::passiveListen);
    button(root,"7 · Inspect USB HID report descriptor (read-only)",this::inspectHid);
    
    add(root,text("DIRECT sends handshake + exactly one ReadDataByIdentifier (0x22) frame, with sequence 0 and no mandatory TesterPresent. PUBLISHED replays the exact documented battery part-number request. Legacy CID 3C may be unavailable on modern bikes. GUIDED is the old experimental flow. All flows stop on 00 FF; passive sends no diagnostic commands.",12,0xffffd08b));

    button(root,"Stop requests",()->{cancel=true;post("Stop requested.");});
    progressText=text("Read: 0 confirmed",14,0xffc9ddf6);add(root,progressText);
    resultText=text("Results appear here",13,0xffd5f5cd);
    resultText.setTextIsSelectable(true);add(root,resultText);
    button(root,"Save full diagnostic JSON",this::saveReport);
    button(root,"Copy report to clipboard",this::copyReport);
    button(root,"Clear screen / results",this::clearScreen);
    add(root,text("RAW USB TRACE (contains serials and device IDs; review before sharing)",12,0xffffd08b));
    traceText=text("",12,0xffa9c7ea);traceText.setTypeface(Typeface.MONOSPACE);
    traceText.setTextIsSelectable(true);add(root,traceText);
    add(root,text("Read-only: USB handshake, UDS ReadDataByIdentifier (0x22), experimental TesterPresent (0x3E), and standard USB descriptor inspection. No configuration writes, resets, flash, or speed-limit edits.",12,0xffa6b9cf));
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
    if(selected==null){updateState("Bosch 108C:0155 not found. Check USB OTG and cable.",false);return;}
    device=selected;post("Detected USB device "+device.getDeviceName()+" with "+device.getInterfaceCount()+" interface(s).");
    if(manager.hasPermission(device)){worker.execute(()->open(device));return;}
    Intent intent=new Intent(USB_ACTION).setPackage(getPackageName());
    int flags=PendingIntent.FLAG_UPDATE_CURRENT;
    if(Build.VERSION.SDK_INT>=31)flags|=PendingIntent.FLAG_MUTABLE;
    PendingIntent pending=PendingIntent.getBroadcast(this,0,intent,flags);
    manager.requestPermission(device,pending);
    updateState("Accept the Android USB permission prompt",false);
  }
  private synchronized void open(UsbDevice target){
    if(busy)return;
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
  private void runProbe(){startJob(()->{
    try{
      byte[] r=exchange(Protocol.handshake(),"Bosch handshake");
      String code=Protocol.summary(r,0);
      if(!code.equals("HANDSHAKE_SLIDING_WINDOW_A"))throw new Exception("Unexpected handshake: "+code);
      updateState("USB handshake confirmed: sliding window A",true);
    }catch(Exception e){updateState("Handshake error: "+e.getMessage(),false);}
  });}

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
        StringBuilder ids=new StringBuilder();
        for(int i=0;i<n-1;i++){
          if((raw[i]&255)==0x85){if(ids.length()>0)ids.append(", ");
            ids.append(String.format(Locale.US,"0x%02X",raw[i+1]&255));i++;}
        }
        String details="HID report descriptor: "+n+" bytes; declared Report IDs="+
          (ids.length()==0?"none identified":ids.toString())+
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
      root.put("app","Bosch BES2 USB Lab 4.1 (experimental read-only)");
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
