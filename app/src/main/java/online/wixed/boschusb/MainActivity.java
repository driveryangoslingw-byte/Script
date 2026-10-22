package online.wixed.boschusb;
import android.app.*;
import android.os.*;
import android.content.*;
import android.hardware.usb.*;
import android.view.*;
import android.widget.*;
import java.util.*;
import java.io.*;
public final class MainActivity extends Activity {
 private static final int VID=0x108c,PID=0x0155,N=64;
 private static final String PERMISSION="online.wixed.boschusb.permission.USB";
 private UsbManager manager;
 private UsbDevice device;
 private UsbDeviceConnection connection;
 private UsbInterface iface;
 private UsbEndpoint in,out;
 private TextView log;
 private final Handler ui=new Handler(Looper.getMainLooper());
 private final java.util.concurrent.ExecutorService worker=java.util.concurrent.Executors.newSingleThreadExecutor();
 private volatile boolean running=false;
 private final BroadcastReceiver receiver=new BroadcastReceiver(){
  @Override public void onReceive(Context c,Intent i){
   if(!PERMISSION.equals(i.getAction()))return;
   UsbDevice received;
   if(Build.VERSION.SDK_INT>=33)
    received=i.getParcelableExtra(UsbManager.EXTRA_DEVICE,UsbDevice.class);
   else
    received=(UsbDevice)i.getParcelableExtra(UsbManager.EXTRA_DEVICE);
   if(received!=null&&(received.getVendorId()!=VID||received.getProductId()!=PID)){
    say("USB permission response ignored: another device");return;
   }
   boolean granted=i.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED,false);
   boolean actual=device!=null&&manager.hasPermission(device);
   say("USB permission: callback="+granted+", actual="+actual);
   if(actual)worker.execute(()->open());
   else say("Permission unavailable; reconnect USB and select Allow.");
  }
 };
 @Override public void onCreate(Bundle state){
  super.onCreate(state);
  manager=(UsbManager)getSystemService(USB_SERVICE);
  LinearLayout root=new LinearLayout(this);
  root.setOrientation(LinearLayout.VERTICAL);root.setPadding(18,18,18,18);root.setBackgroundColor(0xff0b1424);
  TextView title=new TextView(this);title.setText("BOSCH USB LAB  •  108C:0155");title.setTextSize(23);title.setTextColor(0xff69c9ff);root.addView(title);
  button(root,"1. Find Bosch / Connect",()->scan());
  button(root,"2. USB handshake (read only)",()->worker.execute(()->probe()));
  button(root,"3. Read firmware DID 0220",()->worker.execute(()->readDid()));
  TextView status=new TextView(this);status.setText("ASSIST SPEED LIMIT: not decoded. Bosch HMI is not the drive unit; no verified write protocol yet.");status.setTextColor(0xffffc672);status.setTextSize(13);status.setPadding(4,8,4,8);root.addView(status);
  button(root,"4. Copy log",()->{
   ((android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Bosch log",log.getText()));
   Toast.makeText(this,"Log copied",Toast.LENGTH_SHORT).show();
  });
  ScrollView sv=new ScrollView(this);log=new TextView(this);log.setTextColor(0xffd1e3f3);log.setTextSize(14);log.setTextIsSelectable(true);log.setPadding(8,14,8,14);
  sv.addView(log);root.addView(sv,new LinearLayout.LayoutParams(-1,0,1));setContentView(root);
  IntentFilter f=new IntentFilter(PERMISSION);
  if(Build.VERSION.SDK_INT>=33)registerReceiver(receiver,f,Context.RECEIVER_EXPORTED);else registerReceiver(receiver,f);
  say("Connect the display using a USB data cable. This build is diagnostic only and cannot rewrite firmware/settings.");
 }
 private void button(LinearLayout root,String name,Runnable cb){
  Button b=new Button(this);b.setText(name);b.setOnClickListener(v->cb.run());root.addView(b,new LinearLayout.LayoutParams(-1,-2));
 }
 private void say(String s){ui.post(()->{if(log!=null)log.append(s+"\n");});}
 private String hex(byte[] b,int n){
  StringBuilder s=new StringBuilder();for(int j=0;j<n;j++)s.append(String.format(java.util.Locale.US,"%02X ",b[j]&255));return s.toString();
 }
 private void scan(){
  device=null;for(UsbDevice d:manager.getDeviceList().values())if(d.getVendorId()==VID&&d.getProductId()==PID){device=d;break;}
  if(device==null){say("Bosch 108C:0155 not found. Check USB OTG/host mode.");return;}
  say("Found: "+device.getDeviceName());
  if(manager.hasPermission(device)){worker.execute(()->open());return;}
  Intent i=new Intent(PERMISSION).setPackage(getPackageName());
  int flags=PendingIntent.FLAG_UPDATE_CURRENT;
  if(Build.VERSION.SDK_INT>=31)flags|=PendingIntent.FLAG_MUTABLE;
  PendingIntent p=PendingIntent.getBroadcast(this,0,i,flags);
  manager.requestPermission(device,p);
 }
 private synchronized void open(){
  try{
   close();
   if(device==null||!manager.hasPermission(device)){say("No USB permission");return;}
   connection=manager.openDevice(device);if(connection==null)throw new Exception("openDevice failed");
   for(int i=0;i<device.getInterfaceCount();i++){
    UsbInterface f=device.getInterface(i);UsbEndpoint rx=null,tx=null;
    for(int j=0;j<f.getEndpointCount();j++){
     UsbEndpoint e=f.getEndpoint(j);
     if(e.getType()==UsbConstants.USB_ENDPOINT_XFER_INT){
      if(e.getDirection()==UsbConstants.USB_DIR_IN)rx=e;
      else if(e.getDirection()==UsbConstants.USB_DIR_OUT)tx=e;
     }
    }
    if(rx!=null&&connection.claimInterface(f,true)){iface=f;in=rx;out=tx;say("HID opened, interface "+i);return;}
   }
   throw new Exception("No supported interrupt IN endpoint");
  }catch(Exception e){say("Open error: "+e.getMessage());close();}
 }
 private void close(){if(connection!=null){try{if(iface!=null)connection.releaseInterface(iface);}catch(Exception ignored){}connection.close();}connection=null;iface=null;in=null;out=null;}
 private byte[] exchange(byte[] send)throws Exception{
  if(connection==null||in==null||iface==null)throw new Exception("Connect first");
  say("OUT "+hex(send,N));
  int n=out!=null?connection.bulkTransfer(out,send,N,1400):connection.controlTransfer(0x21,9,0x0200,iface.getId(),send,N,1400);
  if(n!=N)throw new Exception("USB OUT failed "+n);
  byte[] recv=new byte[N];int len=connection.bulkTransfer(in,recv,N,1600);
  if(len<=0)throw new Exception("No USB response: "+len);
  say("IN  "+hex(recv,len));
  if(len>1&&recv[0]==0&&(recv[1]&255)==255)throw new Exception("Bosch returned 00 FF (transport/session rejection)");
  return java.util.Arrays.copyOf(recv,len);
 }
 private void probe(){
  if(running)return;running=true;
  try{
   byte[] q=new byte[N];q[2]=3;q[3]=3;q[4]=2;q[5]=1;
   byte[] ans=exchange(q);
   if(ans.length<3||ans[0]!=0||ans[1]!=1||ans[2]!=2)throw new Exception("Unexpected handshake");
   say("Handshake OK: sliding window A");
  }catch(Exception e){say("Handshake error: "+e.getMessage());}
  finally{running=false;}
 }
 private void readDid(){
  if(running)return;running=true;
  try{
   byte[] q=new byte[N];q[2]=3;q[3]=3;q[4]=2;q[5]=1;
   byte[] h=exchange(q);
   if(h.length<3||h[0]!=0||h[1]!=1||h[2]!=2)throw new Exception("Handshake not accepted");
   byte[] d=new byte[N];
   int[] p={2,0,7,0,255,255,11,0,0,0,0x3A,4,3,0x22,2,0x20};
   for(int i=0;i<p.length;i++)d[i]=(byte)p[i];
   byte[] a=exchange(d);
   boolean found=false;
   for(int i=0;i<a.length-3;i++)if((a[i]&255)==0x62&&(a[i+1]&255)==2&&(a[i+2]&255)==0x20){say("Firmware DID 0220: "+hex(java.util.Arrays.copyOfRange(a,i+3,a.length),a.length-i-3));found=true;break;}
   if(!found)say("No confirmed UDS reply 62 02 20; do not interpret as settings.");
  }catch(Exception e){say("Read error: "+e.getMessage());}finally{running=false;}
 }
 @Override public void onDestroy(){try{unregisterReceiver(receiver);}catch(Exception ignored){} worker.execute(this::close);worker.shutdown();super.onDestroy();}
}
