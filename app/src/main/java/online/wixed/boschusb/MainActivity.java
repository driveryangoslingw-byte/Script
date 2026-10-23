package online.wixed.boschusb;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Typeface;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbEndpoint;
import android.hardware.usb.UsbInterface;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Bosch USB Lab 3.0 — read-only, experimental Intuvia/BES2 USB diagnostics.
 * No authentication brute-force, firmware writes, resets or tuning commands.
 */
public final class MainActivity extends Activity {
    private static final String PERMISSION_ACTION = "online.wixed.boschusb.USB_PERMISSION";
    private static final int SAVE_JSON = 318;
    private static final int COLOR_BG = 0xFF101B2B;
    private static final int COLOR_TEXT = 0xFFE7F0FF;
    private static final int COLOR_MUTED = 0xFFB2C5DA;
    private static final int COLOR_ACCENT = 0xFF7CD5FF;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final StringBuilder textLog = new StringBuilder();
    private final ArrayList<JSONObject> packetLog = new ArrayList<>();
    private final ArrayList<JSONObject> results = new ArrayList<>();

    private UsbManager manager;
    private volatile UsbDevice device;
    private UsbDeviceConnection connection;
    private UsbInterface usbInterface;
    private UsbEndpoint epIn;
    private UsbEndpoint epOut;

    private TextView stateView, progressView, resultsView, traceView;
    private Spinner cidSpinner, variantSpinner, timeoutSpinner;
    private EditText didInput;
    private CheckBox extraDids;
    private volatile boolean busy = false;
    private volatile boolean cancel = false;
    private String exportJson = "";

    private interface Job { void run() throws Exception; }

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if (PERMISSION_ACTION.equals(action)) {
                UsbDevice d = usbDeviceFrom(intent);
                if (d == null) d = device;
                if (d == null || d.getVendorId() != Protocol.VID || d.getProductId() != Protocol.PID)
                    return;
                // Never trust the broadcast's permission flag alone.
                if (manager.hasPermission(d)) {
                    final UsbDevice target = d;
                    startJob("Открытие USB", () -> {
                        openDevice(target);
                        setState("USB подключён. Можно запускать проверку.", true);
                    });
                } else {
                    setState("Доступ к USB не предоставлен", false);
                }
            } else if (UsbManager.ACTION_USB_DEVICE_DETACHED.equals(action)) {
                UsbDevice d = usbDeviceFrom(intent);
                if (d != null && d.getVendorId() == Protocol.VID && d.getProductId() == Protocol.PID) {
                    cancel = true;
                    setState("Intuvia отключён от USB", false);
                }
            }
        }
    };

    private UsbDevice usbDeviceFrom(Intent intent) {
        if (Build.VERSION.SDK_INT >= 33)
            return intent.getParcelableExtra(UsbManager.EXTRA_DEVICE, UsbDevice.class);
        return (UsbDevice) intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);
    }

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        manager = (UsbManager) getSystemService(USB_SERVICE);
        IntentFilter filter = new IntentFilter(PERMISSION_ACTION);
        filter.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
        if (Build.VERSION.SDK_INT >= 33)
            registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED);
        else
            registerReceiver(receiver, filter);
        buildUi();
        log("Bosch USB Lab 3.0 · экспериментальный read-only режим.");
        log("Подтверждён только USB handshake; точный формат UDS-ответов на этом байке ещё неизвестен.");
    }

    private TextView text(String s, int sp, int color) {
        TextView v = new TextView(this);
        v.setText(s);
        v.setTextSize(sp);
        v.setTextColor(color);
        v.setPadding(5, 9, 5, 9);
        return v;
    }

    private void add(LinearLayout root, View child) {
        root.addView(child, new LinearLayout.LayoutParams(-1, -2));
    }

    private void addButton(LinearLayout root, String caption, Runnable onClick) {
        Button b = new Button(this);
        b.setText(caption);
        b.setAllCaps(false);
        b.setTextColor(0xFF152437);
        b.setOnClickListener(v -> onClick.run());
        add(root, b);
    }

    private Spinner spinner(LinearLayout root, String[] entries) {
        Spinner s = new Spinner(this);
        ArrayAdapter<String> adapter = new ArrayAdapter<>(
                this, android.R.layout.simple_spinner_item, entries);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        s.setAdapter(adapter);
        s.setBackgroundColor(0xFFE4ECF5);
        add(root, s);
        return s;
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(COLOR_BG);
        root.setPadding(22, 20, 22, 40);
        scroll.addView(root);

        TextView heading = text("BOSCH USB LAB 3.0", 24, COLOR_ACCENT);
        heading.setTypeface(null, Typeface.BOLD);
        add(root, heading);
        add(root, text("Intuvia / BES2 · 108C:0155 · Xiaomi Pad / Android USB Host", 13, COLOR_MUTED));

        stateView = text("USB не подключён", 17, 0xFFFFCB80);
        add(root, stateView);
        addButton(root, "1. Найти и подключить Intuvia", this::connect);
        addButton(root, "2. Проверить USB handshake", this::checkHandshake);

        add(root, text("АВТОМАТИЧЕСКАЯ ДИАГНОСТИКА", 16, COLOR_ACCENT));
        add(root, text("21 тест: 7 опубликованных CID × 3 варианта Seq. Каждый тест начинает новый USB-сеанс. Отказ 00 FF сохраняется в отчёте, но не завершает весь поиск.", 13, COLOR_MUTED));
        extraDids = new CheckBox(this);
        extraDids.setText("Дополнительно проверить 5 DID, если найден UDS-паттерн");
        extraDids.setTextColor(COLOR_TEXT);
        extraDids.setChecked(false);
        add(root, extraDids);
        addButton(root, "3. АВТО · проверить все 21 комбинацию", this::runAuto);

        add(root, text("РУЧНАЯ ПРОВЕРКА", 16, COLOR_ACCENT));
        add(root, text("Адрес CID (назначение компонента пока не подтверждено)", 13, COLOR_MUTED));
        String[] cids = new String[Protocol.CIDS.length];
        for (int i = 0; i < cids.length; i++) cids[i] = "CID " + Protocol.cidHex(Protocol.CIDS[i]);
        cidSpinner = spinner(root, cids);
        cidSpinner.setSelection(3); // 3A was previously tested on this bike.
        add(root, text("Вариант инициализации / Seq", 13, COLOR_MUTED));
        Protocol.Variant[] variants = Protocol.Variant.values();
        String[] names = new String[variants.length];
        for (int i = 0; i < variants.length; i++) names[i] = variants[i].title;
        variantSpinner = spinner(root, names);
        variantSpinner.setSelection(1);

        add(root, text("DID в HEX (по умолчанию 0220)", 13, COLOR_MUTED));
        didInput = new EditText(this);
        didInput.setSingleLine(true);
        didInput.setText("0220");
        didInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS);
        didInput.setTextColor(0xFF152437);
        didInput.setBackgroundColor(0xFFE4ECF5);
        didInput.setPadding(18, 12, 18, 12);
        add(root, didInput);

        add(root, text("Время ожидания USB-ответа", 13, COLOR_MUTED));
        timeoutSpinner = spinner(root, new String[]{
            "Быстро · 600 мс", "Обычно · 1100 мс", "Медленно · 1800 мс"
        });
        timeoutSpinner.setSelection(1);
        addButton(root, "4. Проверить TesterPresent выбранного CID", () -> runManual(false));
        addButton(root, "5. TesterPresent + чтение выбранного DID", () -> runManual(true));
        addButton(root, "6. Прямое чтение DID без TesterPresent", this::runDirectRead);
        addButton(root, "ОСТАНОВИТЬ проверку", () -> {
            cancel = true;
            log("Пользователь запросил остановку.");
        });

        progressView = text("Готово к диагностике", 14, COLOR_ACCENT);
        add(root, progressView);
        add(root, text("РЕЗУЛЬТАТЫ", 15, COLOR_ACCENT));
        resultsView = text("Здесь появятся ответы по CID и DID.", 13, 0xFFCCF4CE);
        resultsView.setTextIsSelectable(true);
        add(root, resultsView);

        addButton(root, "Сохранить полный JSON-отчёт", this::saveReport);
        addButton(root, "Копировать JSON-отчёт", this::copyReport);
        addButton(root, "Очистить результаты и лог", this::clearAll);
        add(root, text("ПОЛНЫЙ USB HEX-ЛОГ", 15, COLOR_ACCENT));
        traceView = text("", 11, COLOR_MUTED);
        traceView.setTypeface(Typeface.MONOSPACE);
        traceView.setTextIsSelectable(true);
        add(root, traceView);
        add(root, text(
            "Важно: 7E / 7F / 62 внутри ответа — пока лишь кандидаты UDS, не доказательство правильного Bosch wrapper. Длинные ответы сохраняются как HEX; неподтверждённый flow-control не отправляется. Логи могут содержать серийные номера. Никаких команд изменения настроек, скорости или прошивки.",
            12, 0xFFFFD18E));
        setContentView(scroll);
    }

    private void log(String message) {
        synchronized (textLog) {
            textLog.append(new Date()).append("  ").append(message).append('\n');
            if (textLog.length() > 350000)
                textLog.delete(0, textLog.length() - 300000);
        }
        ui.post(() -> {
            if (traceView != null) {
                String value;
                synchronized (textLog) { value = textLog.toString(); }
                traceView.setText(value.length() > 35000
                    ? value.substring(value.length() - 35000) : value);
            }
        });
    }

    private void setState(String state, boolean ok) {
        log(state);
        ui.post(() -> {
            stateView.setText(state);
            stateView.setTextColor(ok ? 0xFFAEF5BC : 0xFFFFCB80);
        });
    }

    private void progress(String value) {
        ui.post(() -> progressView.setText(value));
    }

    private void showResult(String line) {
        ui.post(() -> resultsView.append("\n" + line));
    }

    private void startJob(String name, Job job) {
        if (busy) {
            log("Дождитесь завершения текущей операции.");
            return;
        }
        busy = true;
        cancel = false;
        worker.execute(() -> {
            try {
                log("НАЧАЛО: " + name);
                job.run();
                if (cancel) setState("Остановлено пользователем", false);
                log("КОНЕЦ: " + name);
            } catch (Exception e) {
                setState(name + ": " + e.getMessage(), false);
            } finally {
                busy = false;
            }
        });
    }

    private UsbDevice findTarget() {
        for (UsbDevice d : manager.getDeviceList().values()) {
            if (d.getVendorId() == Protocol.VID && d.getProductId() == Protocol.PID)
                return d;
        }
        return null;
    }

    private void connect() {
        if (busy) { log("USB занят диагностикой."); return; }
        UsbDevice d = findTarget();
        if (d == null) {
            setState("Intuvia 108C:0155 не найден. Проверьте OTG и USB-кабель.", false);
            return;
        }
        device = d;
        log("Обнаружен 108C:0155; интерфейсов: " + d.getInterfaceCount());
        if (manager.hasPermission(d)) {
            startJob("Открытие USB", () -> {
                openDevice(d);
                setState("USB подключён; готов к handshake", true);
            });
            return;
        }
        Intent intent = new Intent(PERMISSION_ACTION).setPackage(getPackageName());
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_MUTABLE;
        PendingIntent pending = PendingIntent.getBroadcast(this, 0, intent, flags);
        manager.requestPermission(d, pending);
        setState("Разрешите USB-доступ в системном окне Android", false);
    }

    private UsbDevice requireTarget() throws Exception {
        UsbDevice d = findTarget();
        if (d == null) throw new Exception("108C:0155 отсутствует; подключите USB");
        if (!manager.hasPermission(d)) throw new Exception("Сначала нажмите «Подключить» и разрешите USB");
        return d;
    }

    private synchronized void openDevice(UsbDevice d) throws Exception {
        closeConnection();
        if (d == null || !manager.hasPermission(d))
            throw new Exception("Нет разрешения на USB");
        for (int i = 0; i < d.getInterfaceCount(); i++) {
            UsbInterface f = d.getInterface(i);
            UsbEndpoint read = null, write = null;
            for (int j = 0; j < f.getEndpointCount(); j++) {
                UsbEndpoint ep = f.getEndpoint(j);
                if (ep.getType() != UsbConstants.USB_ENDPOINT_XFER_INT) continue;
                if (ep.getDirection() == UsbConstants.USB_DIR_IN) read = ep;
                if (ep.getDirection() == UsbConstants.USB_DIR_OUT) write = ep;
            }
            if (read == null) continue;
            UsbDeviceConnection c = manager.openDevice(d);
            if (c == null) throw new Exception("Android не открыл USB");
            if (!c.claimInterface(f, true)) { c.close(); continue; }
            connection = c;
            usbInterface = f;
            epIn = read;
            epOut = write;
            device = d;
            log("USB открыт: IN=0x" + Protocol.cidHex(read.getAddress())
                + " OUT=" + (write == null ? "HID control fallback"
                : "0x" + Protocol.cidHex(write.getAddress())));
            return;
        }
        throw new Exception("Не найден interrupt IN endpoint");
    }

    private synchronized void closeConnection() {
        if (connection != null) {
            try { if (usbInterface != null) connection.releaseInterface(usbInterface); }
            catch (Exception ignored) {}
            try { connection.close(); } catch (Exception ignored) {}
        }
        connection = null;
        usbInterface = null;
        epIn = null;
        epOut = null;
    }

    private void record(String direction, String label, byte[] bytes) {
        String hex = Protocol.hex(bytes);
        log(direction + " [" + label + "]" + (hex.isEmpty() ? "" : "\n" + hex));
        JSONObject row = new JSONObject();
        try {
            row.put("time_ms", System.currentTimeMillis());
            row.put("direction", direction);
            row.put("label", label);
            row.put("hex", hex);
            synchronized (packetLog) { packetLog.add(row); }
        } catch (Exception e) { log("Ошибка записи пакета: " + e.getMessage()); }
    }

    private static final class Exchange {
        Protocol.Reply best = Protocol.timeout();
        boolean handshake = false;
        int frames = 0;
        final ArrayList<String> rxHex = new ArrayList<>();
    }

    private Exchange exchange(byte[] tx, String label, int service, int did, int timeoutMs)
            throws Exception {
        if (cancel) throw new Exception("Остановлено");
        if (connection == null || epIn == null || usbInterface == null)
            throw new Exception("USB не открыт");
        record("OUT", label, tx);
        int sent = epOut != null
            ? connection.bulkTransfer(epOut, tx, tx.length, 1600)
            : connection.controlTransfer(0x21, 0x09, 0x0200,
                    usbInterface.getId(), tx, tx.length, 1600);
        if (sent != tx.length) throw new Exception("Ошибка USB OUT: " + sent);

        Exchange result = new Exchange();
        // Preserve all received frames, including transport errors and
        // possible ISO-TP fragments. Never silently discard 00 FF.
        for (int i = 0; i < 4 && !cancel; i++) {
            byte[] buffer = new byte[Protocol.LEN];
            int n = connection.bulkTransfer(epIn, buffer, buffer.length,
                    i == 0 ? timeoutMs : 130);
            if (n <= 0) break;
            byte[] rx = Arrays.copyOf(buffer, n);
            record("IN", label + " #" + (i + 1), rx);
            result.frames++;
            result.rxHex.add(Protocol.hex(rx));
            Protocol.Reply parsed = Protocol.analyze(rx, service, did);
            if (parsed.rank() > result.best.rank()) result.best = parsed;
            if (Protocol.isHandshake(rx)) result.handshake = true;
            log("  => " + parsed.kind + " " + parsed.note
                + (parsed.offset >= 0 ? " / offset " + parsed.offset : ""));
            if (result.handshake || parsed.udsCandidate()) break;
        }
        if (result.frames == 0) record("NO_RX", label, new byte[0]);
        return result;
    }

    private int timeout() {
        int i = timeoutSpinner.getSelectedItemPosition();
        return i == 0 ? 600 : i == 2 ? 1800 : 1100;
    }

    private int manualDid() {
        String s = didInput.getText().toString().trim()
                .replace("0x", "").replace("0X", "").replace(" ", "");
        if (!s.matches("[0-9a-fA-F]{1,4}"))
            throw new IllegalArgumentException("DID должен быть HEX от 0000 до FFFF");
        return Integer.parseInt(s, 16);
    }

    private void checkHandshake() {
        final int timeout = timeout();
        startJob("USB handshake", () -> {
            openDevice(requireTarget());
            Exchange h = exchange(Protocol.handshake(), "HANDSHAKE", 0, 0, timeout);
            if (!h.handshake)
                throw new Exception("Нет ответа 00 01 02; результат: " + h.best.kind);
            setState("Handshake подтверждён: Sliding Window A", true);
            showResult("USB 108C:0155 → 00 01 02 · HANDSHAKE OK");
        });
    }

    private boolean startSession(UsbDevice target, int cid, Protocol.Variant variant,
                                 int timeoutMs) throws Exception {
        if (cancel) return false;
        // Reopening USB is a best-effort fresh session, not a hardware reset.
        openDevice(target);
        Exchange hello = exchange(Protocol.handshake(),
                "Handshake CID " + Protocol.cidHex(cid) + " / " + variant.name(),
                0, 0, timeoutMs);
        if (!hello.handshake) {
            log("Handshake failed: " + hello.best);
            return false;
        }
        if (variant.initial) {
            Exchange empty = exchange(Protocol.initial(),
                    "Experimental initial empty Seq 0", 0, 0, timeoutMs);
            log("Пустой кадр: " + empty.best
                    + ". Даже 00 FF не отменяет следующий TesterPresent.");
        }
        return true;
    }

    private JSONObject baseResult(int cid, Protocol.Variant variant) throws Exception {
        JSONObject item = new JSONObject();
        item.put("cid", Protocol.cidHex(cid));
        item.put("variant", variant.name());
        item.put("request_seq", variant.testerSeq);
        item.put("hardware_proven", false);
        return item;
    }

    private void store(JSONObject item) {
        synchronized (results) { results.add(item); }
    }

    private void readOne(JSONObject item, int cid, int did, int seq, int timeoutMs,
                         String label) throws Exception {
        Exchange answer = exchange(Protocol.readDid(cid, did, seq), label, 0x22, did, timeoutMs);
        JSONObject entry = new JSONObject();
        entry.put("did", Protocol.didHex(did));
        entry.put("label", Protocol.didLabel(did));
        entry.put("seq", seq);
        entry.put("status", answer.best.kind);
        entry.put("note", answer.best.note);
        entry.put("strong_wrapper_candidate", answer.best.strong);
        entry.put("rx", new JSONArray(answer.rxHex));
        JSONArray reads = item.optJSONArray("reads");
        if (reads == null) {
            reads = new JSONArray();
            item.put("reads", reads);
        }
        reads.put(entry);
        showResult("  DID " + Protocol.didHex(did) + " Seq " + seq
                + " → " + answer.best.kind);
        if (answer.best.udsCandidate())
            setState("Есть UDS-паттерн DID " + Protocol.didHex(did)
                + ". Проверьте полный HEX.", true);
    }

    private void runAuto() {
        final int timeoutMs = timeout();
        final boolean more = extraDids.isChecked();
        startJob("Автопроверка 21 комбинации", () -> {
            UsbDevice target = requireTarget();
            int completed = 0, candidates = 0, rejected = 0, didCandidates = 0;
            for (int cid : Protocol.CIDS) {
                for (Protocol.Variant variant : Protocol.Variant.values()) {
                    if (cancel) break;
                    completed++;
                    progress("Авто " + completed + "/21 · CID "
                            + Protocol.cidHex(cid) + " · " + variant.name());
                    JSONObject item = baseResult(cid, variant);
                    try {
                        if (!startSession(target, cid, variant, timeoutMs)) {
                            item.put("status", "HANDSHAKE_FAILED");
                            showResult("CID " + Protocol.cidHex(cid) + " " + variant.name()
                                    + " → HANDSHAKE_FAILED");
                            store(item);
                            continue;
                        }
                        Exchange tp = exchange(
                            Protocol.testerPresent(cid, variant.testerSeq),
                            "TesterPresent CID " + Protocol.cidHex(cid)
                                + " Seq " + variant.testerSeq,
                            0x3E, 0, timeoutMs);
                        item.put("tester_status", tp.best.kind);
                        item.put("tester_note", tp.best.note);
                        item.put("tester_rx", new JSONArray(tp.rxHex));
                        item.put("tester_wrapper_candidate", tp.best.strong);
                        showResult("CID " + Protocol.cidHex(cid) + " " + variant.name()
                            + " · TP → " + tp.best.kind);
                        if (tp.best.kind.equals("TRANSPORT_00_FF")) rejected++;
                        if (tp.best.udsCandidate()) {
                            candidates++;
                            // First test the expected next sequence, then the same
                            // sequence if the first DID did not show a UDS pattern.
                            int nextSeq = (variant.testerSeq + 1) & 0xFF;
                            readOne(item, cid, 0x0220, nextSeq, timeoutMs, "DID 0220 next Seq");
                            JSONArray reads = item.optJSONArray("reads");
                            JSONObject first = reads.getJSONObject(0);
                            if (!first.optString("status").equals("UDS_POSITIVE_CANDIDATE")
                                    && !first.optString("status").equals("UDS_NEGATIVE_CANDIDATE")
                                    && !cancel) {
                                readOne(item, cid, 0x0220, variant.testerSeq,
                                        timeoutMs, "DID 0220 same Seq (experimental)");
                            }
                            if (more) {
                                int seq = (nextSeq + 1) & 0xFF;
                                for (int did : Protocol.EXTRA_DIDS) {
                                    if (cancel) break;
                                    readOne(item, cid, did, seq, timeoutMs,
                                            "Additional read-only DID " + Protocol.didHex(did));
                                    seq = (seq + 1) & 0xFF;
                                }
                            }
                            JSONArray allReads = item.optJSONArray("reads");
                            if (allReads != null) {
                                for (int k = 0; k < allReads.length(); k++)
                                    if (allReads.getJSONObject(k).optString("status")
                                            .equals("UDS_POSITIVE_CANDIDATE"))
                                        didCandidates++;
                            }
                        }
                    } catch (Exception e) {
                        if (cancel) break;
                        item.put("error", e.toString());
                        log("CID " + Protocol.cidHex(cid) + ": " + e.getMessage());
                    }
                    store(item);
                    if (!cancel) Thread.sleep(220);
                }
                if (cancel) break;
            }
            String summary = "Завершено " + completed + "/21; UDS-паттерны TP: "
                + candidates + "; DID+: " + didCandidates + "; 00 FF: " + rejected + ".";
            showResult(summary);
            progress(summary);
            setState(cancel ? "Автопроверка остановлена" :
                "Автопроверка закончена. Сохраните JSON-отчёт.", !cancel);
        });
    }

    private void runManual(boolean readAfterTester) {
        final int cid = Protocol.CIDS[cidSpinner.getSelectedItemPosition()];
        final Protocol.Variant variant = Protocol.Variant.values()[variantSpinner.getSelectedItemPosition()];
        final int timeoutMs = timeout();
        final int did;
        try { did = readAfterTester ? manualDid() : 0; }
        catch (Exception e) { Toast.makeText(this, e.getMessage(), Toast.LENGTH_LONG).show(); return; }
        startJob("Ручной TesterPresent", () -> {
            UsbDevice target = requireTarget();
            JSONObject item = baseResult(cid, variant);
            item.put("mode", "manual_tester");
            if (!startSession(target, cid, variant, timeoutMs))
                throw new Exception("Handshake не подтверждён");
            Exchange tp = exchange(Protocol.testerPresent(cid, variant.testerSeq),
                    "Manual TesterPresent", 0x3E, 0, timeoutMs);
            item.put("tester_status", tp.best.kind);
            item.put("tester_rx", new JSONArray(tp.rxHex));
            showResult("Ручной CID " + Protocol.cidHex(cid) + " TP → " + tp.best.kind);
            if (readAfterTester && tp.best.udsCandidate() && !cancel)
                readOne(item, cid, did, (variant.testerSeq + 1) & 0xFF,
                        timeoutMs, "Manual DID after TesterPresent");
            else if (readAfterTester)
                showResult("DID не отправлен: TesterPresent не показал UDS-паттерн. "
                    + "Для прямого чтения есть отдельная кнопка.");
            store(item);
            setState("Ручная проверка завершена; смотрите результаты", true);
        });
    }

    private void runDirectRead() {
        final int cid = Protocol.CIDS[cidSpinner.getSelectedItemPosition()];
        final Protocol.Variant variant = Protocol.Variant.values()[variantSpinner.getSelectedItemPosition()];
        final int timeoutMs = timeout();
        final int did;
        try { did = manualDid(); }
        catch (Exception e) { Toast.makeText(this, e.getMessage(), Toast.LENGTH_LONG).show(); return; }
        startJob("Прямое read-only чтение DID", () -> {
            UsbDevice target = requireTarget();
            JSONObject item = baseResult(cid, variant);
            item.put("mode", "direct_did_without_tester");
            if (!startSession(target, cid, variant, timeoutMs))
                throw new Exception("Handshake не подтверждён");
            readOne(item, cid, did, variant.testerSeq, timeoutMs,
                    "Direct DID without TesterPresent");
            store(item);
            setState("Прямой запрос выполнен; формат ответа может быть неизвестен", true);
        });
    }

    private void clearAll() {
        if (busy) { log("Остановите проверку перед очисткой."); return; }
        synchronized (packetLog) { packetLog.clear(); }
        synchronized (results) { results.clear(); }
        synchronized (textLog) { textLog.setLength(0); }
        ui.post(() -> {
            resultsView.setText("Результаты очищены.");
            traceView.setText("");
            progressView.setText("Готово к диагностике");
        });
    }

    private String report() {
        JSONObject root = new JSONObject();
        try {
            root.put("app", "Bosch USB Lab 3.0");
            root.put("hardware", "Bosch BES2 / Intuvia 108C:0155");
            root.put("read_only", true);
            root.put("uds_wrapper_verified", false);
            root.put("notice", "UDS-like patterns are candidates. Raw USB data may contain serials.");
            synchronized (results) { root.put("results", new JSONArray(results)); }
            synchronized (packetLog) { root.put("usb_trace", new JSONArray(packetLog)); }
            synchronized (textLog) { root.put("text_log", textLog.toString()); }
            return root.toString(2);
        } catch (Exception e) {
            return "{\"error\":\"Unable to build JSON report\"}";
        }
    }

    private void copyReport() {
        ((ClipboardManager) getSystemService(CLIPBOARD_SERVICE))
                .setPrimaryClip(ClipData.newPlainText("Bosch USB diagnostics", report()));
        Toast.makeText(this, "JSON скопирован", Toast.LENGTH_SHORT).show();
    }

    private void saveReport() {
        exportJson = report();
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/json");
        intent.putExtra(Intent.EXTRA_TITLE, "bosch-bes2-usb-lab-3.json");
        startActivityForResult(intent, SAVE_JSON);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != SAVE_JSON || result != RESULT_OK
                || data == null || data.getData() == null) return;
        try (OutputStream out = getContentResolver().openOutputStream(data.getData())) {
            if (out == null) throw new Exception("Не удалось открыть файл");
            out.write(exportJson.getBytes(StandardCharsets.UTF_8));
            log("JSON-отчёт сохранён.");
        } catch (Exception e) {
            setState("Ошибка сохранения JSON: " + e.getMessage(), false);
        }
    }

    @Override protected void onDestroy() {
        cancel = true;
        try { unregisterReceiver(receiver); } catch (Exception ignored) {}
        worker.shutdownNow();
        closeConnection();
        super.onDestroy();
    }
}
