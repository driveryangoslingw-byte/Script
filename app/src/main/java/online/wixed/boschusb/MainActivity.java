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
    post("BES2 read-only diagnostic build. USB 108C:0155.");
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
    TextView head=text("BOSCH BES2  •  DIAGNOSTICS",22,0xff87dcff);
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
    initialCheck=new CheckBox(this);initialCheck.setText("Send optional initial empty frame");
    initialCheck.setTextColor(0xffe4efff);initialCheck.setChecked(false);add(root,initialCheck);
    add(root,text("Published BUI CIDs 39, 3A, 5D are research candidates. Motor/BMS CIDs 3B/3C are LEGACY and may not work on your firmware.",12,0xffffd08b));
    button(root,"3 · Read first DID (safe protocol probe)",()->readData(false));
    button(root,"4 · Read profile DIDs (stops on transport error)",()->readData(true));
    button(root,"AUTO · Discover published CIDs (bounded read-only probe)",this::autoDiscover);
    add(root,text("Tests 7 published CIDs with UDS TesterPresent, then reads a single DID only on a plausible reply. Stops at the first matched DID, after 3 transport rejections, or when you press Stop. This is NOT an authentication brute-force.",12,0xffffd08b));

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
    add(root,text("Read-only: only USB handshake and UDS ReadDataByIdentifier (0x22). No writes, resets, firmware update or speed-limit edits.",12,0xffa6b9cf));
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
    try{row.put("direction",direction);row.put("detail",detail);row.put("hex",h);}catch(Exception ignored){}
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
  private void readData(boolean all){
    final int profile=profileSpinner.getSelectedItemPosition();
    final boolean empty=initialCheck.isChecked();
    startJob(()->{
      try{
        byte[] h=exchange(Protocol.handshake(),"Handshake before diagnostics");
        if(!Protocol.summary(h,0).equals("HANDSHAKE_SLIDING_WINDOW_A"))
          throw new Exception("Handshake rejected: "+Protocol.summary(h,0));
        int seq=0;
        if(empty){
          byte[] a=exchange(Protocol.initial(seq++),"Optional empty initial frame");
          String result=Protocol.summary(a,0);
          post("Initial-frame reply: "+result);
          if(result.equals("TRANSPORT_REJECTED_00_FF"))throw new Exception("00 FF on initial empty frame; try unchecked.");
        }
        int cid=Protocol.cid(profile),n=0;int[] ids=Protocol.dids(profile);
        post("Target: "+Protocol.PROFILES[profile]);
        for(int did:ids){
          if(cancel)break;
          byte[] r=exchange(Protocol.read(cid,did,seq++),"Read DID "+Protocol.didHex(did)+" / CID "+String.format(Locale.US,"%02X",cid));
          String code=Protocol.summary(r,did);
          JSONObject result=new JSONObject();
          result.put("profile",Protocol.PROFILES[profile]);
          result.put("cid",String.format(Locale.US,"%02X",cid));
          result.put("did",Protocol.didHex(did));
          result.put("label",Protocol.label(did));
          result.put("response",code);
          result.put("raw",did==0x0242?"redacted in results (see raw trace)":Protocol.hex(r));
          synchronized(values){values.add(result);}
          n++;
          final String row=Protocol.didHex(did)+"  "+Protocol.label(did)+"  → "+code+"\n";
          ui.post(()->{
            resultText.append(row);
            progressText.setText("Tested: "+values.size()+"  •  latest CID: "+String.format(Locale.US,"%02X",cid));
          });
          if(code.equals("TRANSPORT_REJECTED_00_FF")){
            throw new Exception("00 FF: transport/session rejects this query. DID="+Protocol.didHex(did)+", CID="+String.format(Locale.US,"%02X",cid)+". This is NOT a valid setting value.");
          }
          if(!all)break;
        }
        updateState((cancel?"Stopped":"Read attempt complete")+" (not all raw replies may be decodable)",!cancel);
      }catch(Exception e){updateState("Diagnostics stopped: "+e.getMessage(),false);}
    });
  }

  /**
   * Experimental, bounded discovery of seven documented candidate CIDs.
   * There is no published byte-level spec for the community's heartbeat query.
   * The diagnostic TesterPresent service used here is a distinct, standard UDS
   * request and MUST NOT be reported as confirmed Bosch heartbeat support.
   * Never enumerate authentication codes or issue configuration writes.
   */
  private void autoDiscover(){
    startJob(()->{
      int tries=0,rejections=0,candidates=0,confirmed=0;
      try{
        post("AUTO DISCOVERY started: max "+Protocol.DISCOVERY_CIDS.length+
             " published CID candidates, max "+Protocol.MAX_TRANSPORT_REJECTS+
             " transport rejects, minimum "+Protocol.MIN_PROBE_DELAY_MS+"ms pause.");
        updateState("Auto CID discovery: read-only, limited",true);
        for(int cid:Protocol.DISCOVERY_CIDS){
          if(cancel)break;
          if(rejections>=Protocol.MAX_TRANSPORT_REJECTS){
            post("SAFETY STOP: repeated 00 FF refusals; not a valid CID result.");
            break;
          }
          tries++;
          String cidStr=String.format(Locale.US,"%02X",cid);
          final int completed=tries;
          ui.post(()->progressText.setText("Auto test "+completed+"/"+Protocol.DISCOVERY_CIDS.length+" · CID "+cidStr));
          // Begin a new transport session before each candidate.
          byte[] hello=exchange(Protocol.handshake(),"AUTO handshake for CID "+cidStr);
          String hs=Protocol.summary(hello,0);
          if(!hs.equals("HANDSHAKE_SLIDING_WINDOW_A")){
            post("AUTO STOP: handshake stopped matching the confirmed response: "+hs);
            break;
          }
          int did=(cid==0x39||cid==0x3A||cid==0x5D)?0x0220:0xF130;
          byte[] answer=exchange(Protocol.testerPresent(cid,0),
              "AUTO tester-present (not Bosch heartbeat), CID "+cidStr);
          String status=Protocol.testerSummary(answer);
          final String row="CID "+cidStr+" TesterPresent → "+status;
          post(row);
          ui.post(()->resultText.append(row+"\\n"));
          JSONObject item=new JSONObject();
          item.put("mode","bounded_auto_cid");
          item.put("cid",cidStr);
          item.put("did",Protocol.didHex(did));
          item.put("probe",status);
          item.put("probe_raw",Protocol.hex(answer));
          if(status.equals("TRANSPORT_REJECTED_00_FF")){
            rejections++;
            item.put("result","transport_rejected_not_a_cid_verdict");
          }else if(status.equals("TESTER_PRESENT_CANDIDATE_UNVERIFIED")){
            candidates++;
            byte[] read=exchange(Protocol.read(cid,did,1),"AUTO read DID "+
                    Protocol.didHex(did)+" CID "+cidStr);
            String readStatus=Protocol.summary(read,did);
            item.put("did_status",readStatus);
            item.put("did_raw",Protocol.hex(read));
            post("CID "+cidStr+" DID "+Protocol.didHex(did)+" → "+readStatus);
            ui.post(()->resultText.append("CID "+cidStr+" DID → "+readStatus+"\\n"));
            if(readStatus.equals("UDS_POSITIVE_RAW_UNVERIFIED")){
              confirmed++;
              item.put("result","matched_did_pattern_unverified_transport_frame");
              synchronized(values){values.add(item);}
              updateState("CID "+cidStr+" answered with matching UDS DID bytes; examine trace before trusting.",true);
              post("AUTO STOP: first matching DID candidate CID "+cidStr);
              return;
            }else if(readStatus.equals("TRANSPORT_REJECTED_00_FF")){
              rejections++;
              item.put("result","transport_rejected_after_tester_candidate");
            }else item.put("result","tester_candidate_but_no_matching_did");
          }else item.put("result","no_confirmed_tester_positive");
          synchronized(values){values.add(item);}
          if(cancel||rejections>=Protocol.MAX_TRANSPORT_REJECTS)break;
          try{Thread.sleep(Protocol.MIN_PROBE_DELAY_MS);}
          catch(InterruptedException ie){Thread.currentThread().interrupt();break;}
        }
        String finalStatus="Discovery ended: "+tries+" attempted, "+candidates+
            " tester candidates, "+confirmed+" DID matches, "+rejections+
            " transport refusals.";
        post(finalStatus);
        updateState(finalStatus+" No proven diagnostic channel until DID confirmed.",confirmed>0);
      }catch(Exception e){
        updateState("AUTO STOP: "+e.getMessage()+" (export the log)",false);
      }
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
      root.put("app","Bosch BES2 Diagnostic Lab 2.1 (experimental read only)");
      root.put("usb_vid_pid","108C:0155");
      root.put("hardware_validated",false);
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
