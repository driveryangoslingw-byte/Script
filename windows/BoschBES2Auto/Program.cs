using HidSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BoschBES2Auto;

internal static class Program
{
 [STAThread] static void Main() {
  ApplicationConfiguration.Initialize();
  Application.Run(new DiagnosticWindow());
 }
}
public sealed class DiagnosticWindow : Form
{
 private readonly Button start=new(), stop=new(), reports=new();
 private readonly TextBox logbox=new();
 private readonly Label status=new();
 private readonly object gate=new();
 private readonly List<object> frames=new(), steps=new();
 private readonly List<string> lines=new();
 private CancellationTokenSource? cancel;
 private bool running;
 private string? reportFile;

 public DiagnosticWindow() {
  Text="Bosch BES2 — Windows Auto Diagnostic";
  MinimumSize=new Size(790,530);Size=new Size(1070,740);
  StartPosition=FormStartPosition.CenterScreen;
  BackColor=Color.FromArgb(11,23,39);ForeColor=Color.White;
  Font=new Font("Segoe UI",10);
  var main=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),RowCount=5,ColumnCount=1};
  main.RowStyles.Add(new RowStyle(SizeType.Absolute,55));
  main.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
  main.RowStyles.Add(new RowStyle(SizeType.Absolute,78));
  main.RowStyles.Add(new RowStyle(SizeType.Absolute,67));
  main.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  var heading=new Label{Text="BOSCH BES2  /  WINDOWS ONE-CLICK DIAGNOSTICS",Dock=DockStyle.Fill,
   Font=new Font("Segoe UI",18,FontStyle.Bold),ForeColor=Color.FromArgb(127,209,252)};
  heading.TextAlign=ContentAlignment.MiddleLeft;main.Controls.Add(heading,0,0);
  main.Controls.Add(new Label{Text="Intuvia · HID 108C:0155 · только чтение, без разблокировки и изменения настроек",
   Dock=DockStyle.Fill,ForeColor=Color.LightSteelBlue},0,1);
  var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1};
  row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));
  row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,16));
  row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,24));
  Setup(start,"▶  ЗАПУСТИТЬ ВСЁ",Color.FromArgb(29,126,186));
  Setup(stop,"■  СТОП",Color.FromArgb(79,90,106));
  Setup(reports,"ПАПКА ОТЧЁТОВ",Color.FromArgb(39,71,94));
  stop.Enabled=false;
  start.Click+=(_,_)=>StartRun();
  stop.Click+=(_,_)=>{cancel?.Cancel();Say("STOP запрошен");};
  reports.Click+=(_,_)=>OpenReports();
  row.Controls.Add(start,0,0);row.Controls.Add(stop,1,0);row.Controls.Add(reports,2,0);
  main.Controls.Add(row,0,2);
  status.Text="Включи велосипед с Intuvia, подключи кабель данных к Windows и нажми ЗАПУСТИТЬ.";
  status.ForeColor=Color.Khaki;status.Dock=DockStyle.Fill;
  status.TextAlign=ContentAlignment.MiddleLeft;
  main.Controls.Add(status,0,3);
  logbox.ReadOnly=true;logbox.Multiline=true;logbox.ScrollBars=ScrollBars.Both;
  logbox.WordWrap=false;logbox.Dock=DockStyle.Fill;
  logbox.Font=new Font("Consolas",10);
  logbox.BackColor=Color.FromArgb(8,27,47);logbox.ForeColor=Color.FromArgb(175,222,247);
  main.Controls.Add(logbox,0,4);Controls.Add(main);
  Say("BES2 Windows 1.0. Одно нажатие: HID -> handshake -> 3s passive -> 1 read-only request -> JSON.");
  Say("Windows HID использует байт Report ID=0 перед 64-байтным сообщением Bosch.");
  Say("Ответ 00 FF пока не расшифрован. Программа не будет перебирать случайные CID.");
 }
 private static void Setup(Button b,string title,Color color) {
  b.Text=title;b.Dock=DockStyle.Fill;b.Margin=new Padding(4,10,4,10);
  b.BackColor=color;b.ForeColor=Color.White;b.FlatStyle=FlatStyle.Flat;
  b.FlatAppearance.BorderSize=0;b.Font=new Font("Segoe UI",11,FontStyle.Bold);
 }
 private void UI(Action a) {
  try {if(!IsDisposed&&IsHandleCreated)BeginInvoke(a);}catch(InvalidOperationException){}
 }
 private void Say(string s) {
  string line=$"[{DateTime.Now:HH:mm:ss}] {s}";
  lock(gate){lines.Add(line);}
  UI(()=>{logbox.AppendText(line+Environment.NewLine);logbox.SelectionStart=logbox.TextLength;logbox.ScrollToCaret();});
 }
 private void State(string s){Say(s);UI(()=>status.Text=s);}
 private void Step(string name,string result) {
  lock(gate){steps.Add(new {name,result});}
  Say($"RESULT {name}: {result}");
 }
 private void Trace(string direction,string name,byte[] b) {
  string hex=Protocol.Hex(b);
  lock(gate){frames.Add(new {at=DateTimeOffset.Now.ToString("O"),direction,name,hex});}
  Say($"{direction} {name}: {hex}");
 }
 private void StartRun() {
  if(running)return;
  running=true;cancel=new CancellationTokenSource();reportFile=null;
  lock(gate){frames.Clear();steps.Clear();lines.Clear();}
  logbox.Clear();start.Enabled=false;stop.Enabled=true;
  var token=cancel.Token;
  State("Шаг 1/4: ищу Bosch USB HID 108C:0155...");
  _=Task.Run(()=>Diagnose(token)).ContinueWith(t=>UI(()=>{
   running=false;start.Enabled=true;stop.Enabled=false;
   cancel?.Dispose();cancel=null;
   if(t.IsFaulted)State("Неожиданная ошибка: "+t.Exception?.GetBaseException().Message);
  }));
 }
 private static bool TimedOut(Exception e) => e is TimeoutException ||
  (e is IOException && (e.Message.Contains("timeout",StringComparison.OrdinalIgnoreCase) ||
                         e.Message.Contains("timed out",StringComparison.OrdinalIgnoreCase)));
 private byte[] Exchange(HidStream stream,HidDevice device,byte[] raw,string name,CancellationToken token) {
  token.ThrowIfCancellationRequested();
  var packet=Protocol.ToWindowsReport(raw,device.GetMaxOutputReportLength());
  Trace("OUT_USB64",name,raw);
  stream.WriteTimeout=1600;stream.Write(packet,0,packet.Length);
  stream.ReadTimeout=1900;
  var input=new byte[device.GetMaxInputReportLength()];
  int n=stream.Read(input,0,input.Length);
  var reply=Protocol.FromWindowsReport(input,n);
  Trace("IN_USB64",name,reply);
  return reply;
 }
 private void Diagnose(CancellationToken token) {
  string outcome="Incomplete";
  try {
   var devices=DeviceList.Local.GetHidDevices(Protocol.Vid,Protocol.Pid).ToList();
   Step("USB enumeration","Matching HID interfaces: "+devices.Count);
   if(devices.Count==0)throw new Exception("Bosch HID 108C:0155 не найден. Проверь USB кабель, питание Intuvia и Диспетчер устройств Windows.");
   HidDevice? chosen=null;HidStream? stream=null;
   foreach(var d in devices) {
    token.ThrowIfCancellationRequested();
    int il=d.GetMaxInputReportLength(),ol=d.GetMaxOutputReportLength();
    Say($"Windows HID interface IN={il}, OUT={ol} bytes (including Report ID).");
    if(il!=65||ol!=65){Say("Interface skipped: not the observed 64-byte HID reports.");continue;}
    if(d.TryOpen(out HidStream opened)){chosen=d;stream=opened;break;}
    Say("Cannot open this interface; another software instance may be holding it.");
   }
   if(chosen==null||stream==null)throw new Exception("Не удалось открыть HID 65 байт. Закрой другие программы, работающие с велосипедом.");
   using(stream) {
    Step("USB HID","CONNECTED; no custom driver installed");
    State("Шаг 2/4: handshake 321...");
    byte[] handshake=Exchange(stream,chosen,Protocol.Handshake(),"HandShake 321",token);
    string mode=Protocol.HandshakeResult(handshake);
    Step("Handshake",mode);
    if(mode!="HANDSHAKE_SLIDING_WINDOW_A")throw new Exception("Handshake не подтвердил Sliding Window A; запросов ECU не отправлял.");
    Say("Transport handshake confirmed. Bosch ECU session and CAN routing are NOT verified.");
    State("Шаг 3/4: пассивный приём USB 3 секунды...");
    int passive=0;
    for(int i=0;i<4;i++) {
     token.ThrowIfCancellationRequested();
     stream.ReadTimeout=750;
     var buffer=new byte[chosen.GetMaxInputReportLength()];
     try {
      int n=stream.Read(buffer,0,buffer.Length);
      byte[] packet=Protocol.FromWindowsReport(buffer,n);
      Trace("IN_PASSIVE","window "+(i+1),packet);passive++;
     }catch(Exception e) when(TimedOut(e)) {Say($"Passive {i+1}/4: no incoming report.");}
    }
    Step("Passive capture",passive+" unsolicited frames");
    token.ThrowIfCancellationRequested();
    State("Шаг 4/4: однократное чтение BUI CID 3A / DID 0220...");
    byte[] response=Exchange(stream,chosen,Protocol.ReadBui(),"BUI 3A / UDS 22 02 20",token);
    string result=Protocol.ReadResult(response);
    Step("BUI read",result);
    outcome=result=="TRANSPORT_00_FF_UNDECODED"
      ? "USB подключён, handshake OK, но Bosch вернул 00 FF. Ошибка транспорта НЕ расшифрована. Нужен USB-захват рабочего диагностического сеанса BES2, а не перебор команд."
      : result=="DID_REPLY_CANDIDATE_UNVERIFIED"
       ? "Пришёл возможный ответ DID. Его структура и ECU-источник пока не проверены."
       : "Ответ "+result+". Подтверждённых значений ECU нет, остановка без повторных запросов.";
   }
  } catch(OperationCanceledException) {
   outcome="Остановлено пользователем.";
  } catch(Exception e) {
   outcome="Остановка: "+e.Message;
  } finally {
   Step("FINAL",outcome);
   string? path=SaveReport(outcome);reportFile=path;
   State(outcome);
   if(path!=null)Say("ФАЙЛ СОХРАНЁН: "+path);
   Say("Никаких записей, ресетов, перепрошивки и изменения ограничений скорости.");
  }
 }
 private static string ReportsFolder() {
  string downloads=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","Bosch-BES2");
  try{Directory.CreateDirectory(downloads);return downloads;}
  catch{
   string fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BoschBES2Auto","Reports");
   Directory.CreateDirectory(fallback);return fallback;
  }
 }
 private string? SaveReport(string outcome) {
  try {
   string path=Path.Combine(ReportsFolder(),$"bosch-bes2-win-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
   string json;
   lock(gate){
    json=JsonSerializer.Serialize(new {
     app="Bosch BES2 Windows One-Tap 1.0",time_utc=DateTimeOffset.UtcNow,vid_pid="108C:0155",
     bosch_heartbeat_implemented=false,verified_ecu_values=0,outcome,
     warning="Raw frames may contain serial identifiers. Redact before sharing.",
     steps=steps.ToArray(),trace=frames.ToArray(),log=lines.ToArray()
    },new JsonSerializerOptions{WriteIndented=true});
   }
   File.WriteAllText(path,json);return path;
  }catch(Exception e){Say("ERROR saving JSON: "+e.Message);return null;}
 }
 private void OpenReports() {
  try {
   string path=reportFile==null?ReportsFolder():Path.GetDirectoryName(reportFile)!;
   Process.Start(new ProcessStartInfo("explorer.exe",path){UseShellExecute=true});
  } catch(Exception e){State("Невозможно открыть папку: "+e.Message);}
 }
 protected override void OnFormClosing(FormClosingEventArgs e) {
  if(running){cancel?.Cancel();e.Cancel=true;State("Прерываю тест и сохраняю отчёт; закрой окно после завершения.");return;}
  base.OnFormClosing(e);
 }
}