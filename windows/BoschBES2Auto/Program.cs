using System.Diagnostics;
using System.Text.Json;

namespace BoschBES2Auto;

internal static class Program
{
    public static string ReportsFolder => Path.Combine(AppContext.BaseDirectory, "reports");

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) return RunCommandLine(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new DiagnosticWindow());
        return 0;
    }

    private static int RunCommandLine(string[] args)
    {
        try
        {
            bool enumerate = false, probe = false;
            int delay = 0;
            string directory = ReportsFolder;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--enumerate": enumerate = true; break;
                    case "--probe": probe = true; break;
                    case "--delay-ms" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out delay) || delay < 0 || delay > 3000)
                            throw new ArgumentException("--delay-ms: expected 0..3000.");
                        break;
                    case "--report-dir" when i + 1 < args.Length: directory = Path.GetFullPath(args[++i]); break;
                    case "--help":
                        Console.WriteLine("BoschBES2Auto --enumerate | --probe [--delay-ms 0..3000] [--report-dir PATH]");
                        return 0;
                    default: throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
                }
            }
            if (enumerate == probe) throw new ArgumentException("Specify exactly one of --enumerate or --probe.");
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                var result = new DiagnosticRunner().Run(new DiagnosticOptions {
                    EnumerateOnly = enumerate, HandshakeDelayMs = delay, ReportDirectory = directory
                }, cancel.Token, Console.WriteLine);
                Console.WriteLine($"OUTCOME={result.Outcome}; USB={result.UsbStatus}; HANDSHAKE={result.HandshakeStatus}; VERIFIED_ECU_VALUES={result.VerifiedEcuValues}");
                Console.WriteLine("REPORT_FILE=" + result.ReportFile);
                if (result.ReportSaveError != null) { Console.Error.WriteLine(result.ReportSaveError); return 4; }
                return result.Outcome is "ENUMERATION_ONLY" or "BUI_SOFTWARE_VERSION_READ" ? 0 : result.Outcome == "CANCELLED" ? 130 : 3;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
}

public sealed class DiagnosticWindow : Form
{
    private readonly Button start = new(), stop = new(), reports = new();
    private readonly Label state = new(), levels = new();
    private readonly TextBox logbox = new(), comparison = new();
    private readonly NumericUpDown delay = new() { Minimum = 0, Maximum = 3000, Value = 0, Width = 110 };
    private CancellationTokenSource? cancellation;
    private Task? activeRun;

    public DiagnosticWindow()
    {
        Text = "Bosch BES2 — исследование USB и диагностика";
        MinimumSize = new Size(900, 650); Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 26, 40); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        foreach (int height in new[] { 52, 76, 42, 65, 48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "BOSCH BES2 · USB-ДИАГНОСТИКА", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 19, FontStyle.Bold), ForeColor = Color.LightSkyBlue }, 0, 0);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        Style(start, "ЗАПУСТИТЬ АВТОМАТИЧЕСКУЮ ДИАГНОСТИКУ", Color.FromArgb(25, 112, 172));
        Style(stop, "СТОП", Color.FromArgb(90, 70, 75));
        Style(reports, "ПАПКА ОТЧЁТОВ", Color.FromArgb(42, 72, 91));
        stop.Enabled = false;
        start.Click += (_, _) => StartRun();
        stop.Click += (_, _) => { cancellation?.Cancel(); state.Text = "Останавливаю сеанс и сохраняю отчёт…"; };
        reports.Click += (_, _) => {
            try { Directory.CreateDirectory(Program.ReportsFolder); Process.Start(new ProcessStartInfo("explorer.exe", Program.ReportsFolder) { UseShellExecute = true }); }
            catch (Exception ex) { state.Text = ex.Message; }
        };
        buttons.Controls.Add(start, 0, 0); buttons.Controls.Add(stop, 1, 0); buttons.Controls.Add(reports, 2, 0);
        layout.Controls.Add(buttons, 0, 1);
        levels.Text = "USB: не проверен     Handshake: не проверен     Сессия: не установлена     ECU: 0 значений";
        levels.Dock = DockStyle.Fill; levels.ForeColor = Color.LightSteelBlue;
        layout.Controls.Add(levels, 0, 2);
        state.Text = "Готов. Программа прочитает версию ПО Intuvia и сохранит результаты проверки.";
        state.Dock = DockStyle.Fill; state.ForeColor = Color.Khaki;
        layout.Controls.Add(state, 0, 3);
        layout.Controls.Add(new Label { Text = "Intuvia 108C:0155 · один handshake и один запрос чтения · без изменения настроек\nПроверяется BUI. Диагностика мотора и аккумулятора пока не реализована.", Dock = DockStyle.Fill }, 0, 4);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var logTab = new TabPage("HEX и события API") { BackColor = BackColor };
        PrepareText(logbox); logTab.Controls.Add(logbox); tabs.TabPages.Add(logTab);
        var research = new TabPage("Исследование и сравнение") { BackColor = BackColor };
        var researchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill };
        settings.Controls.Add(new Label { Text = "Задержка после handshake, мс:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        settings.Controls.Add(delay);
        var compare = new Button { Text = "Сравнить JSON…", AutoSize = true };
        compare.Click += (_, _) => CompareReports(); settings.Controls.Add(compare);
        researchLayout.Controls.Add(settings, 0, 0);
        researchLayout.Controls.Add(new Label { Text = "Выбранная задержка применяется к следующему запуску. Автоматического перебора нет.\nЛог содержит вызовы HidSharp, а фактический тип USB-транзакции требует отдельного USBPcap-захвата.", Dock = DockStyle.Fill }, 0, 1);
        PrepareText(comparison); researchLayout.Controls.Add(comparison, 0, 2);
        research.Controls.Add(researchLayout); tabs.TabPages.Add(research);
        layout.Controls.Add(tabs, 0, 5); Controls.Add(layout);
    }

    private static void Style(Button button, string text, Color color)
    {
        button.Text = text; button.Dock = DockStyle.Fill; button.Margin = new Padding(4, 6, 4, 6);
        button.BackColor = color; button.ForeColor = Color.White; button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    }

    private static void PrepareText(TextBox box)
    {
        box.Multiline = true; box.ReadOnly = true; box.WordWrap = false; box.ScrollBars = ScrollBars.Both;
        box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 10);
        box.BackColor = Color.FromArgb(8, 22, 35); box.ForeColor = Color.LightSkyBlue;
    }

    private void AppendLog(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => logbox.AppendText(line + Environment.NewLine)); } catch (InvalidOperationException) { }
    }

    private async void StartRun()
    {
        if (activeRun is { IsCompleted: false }) return;
        start.Enabled = false; stop.Enabled = true; delay.Enabled = false; logbox.Clear();
        cancellation = new CancellationTokenSource();
        state.Text = "Проверяю USB, согласовываю режим и выполняю один запрос чтения…";
        var options = new DiagnosticOptions { HandshakeDelayMs = (int)delay.Value, ReportDirectory = Program.ReportsFolder };
        try
        {
            var run = Task.Run(() => new DiagnosticRunner().Run(options, cancellation.Token, AppendLog));
            activeRun = run;
            var result = await run;
            levels.Text = result.VerifiedEcuValues > 0
                ? $"USB: подключён     Транспорт: Sliding Window A     Сессия: ответ подтверждён     BUI: {result.VerifiedEcuValues} значение"
                : $"USB: {result.UsbStatus}     Handshake: {result.HandshakeStatus}     Сессия: {result.SessionStatus}     ECU: 0";
            state.Text = result.Outcome switch {
                "BUI_SOFTWARE_VERSION_READ" => "Успешно прочитана версия ПО Intuvia: " + string.Join(", ", result.Values.Select(value => value.Value)) + ". Отчёт сохранён.",
                "TRANSPORT_ERROR_00_FF" => "Получен 00 FF. Значение ошибки не расшифровано; чтение ECU не подтверждено. Сеанс закрыт.",
                "NO_DEVICE" => "Bosch 108C:0155 не найден.",
                "CANCELLED" => "Остановлено. Новые запросы не отправляются.",
                "SESSION_ALREADY_RUNNING" => "Другой экземпляр программы выполняет проверку. Новый сеанс не запускался.",
                "UNVERIFIED_FRAMES_OBSERVED" => "Получены кадры. Корректность диагностического ответа и источник ECU ещё не подтверждены.",
                _ => $"Проверка завершена: {result.Outcome}. Подтверждённых значений ECU: 0."
            };
            if (result.ReportSaveError != null) state.Text += " Ошибка сохранения: " + result.ReportSaveError;
            else AppendLog("Отчёт: " + result.ReportFile);
        }
        catch (Exception ex) { state.Text = "Ошибка выполнения: " + ex.Message; }
        finally { activeRun = null; cancellation.Dispose(); cancellation = null; start.Enabled = true; stop.Enabled = false; delay.Enabled = true; }
    }

    private void CompareReports()
    {
        using var picker = new OpenFileDialog { Filter = "Отчёты JSON|*.json", Multiselect = true, InitialDirectory = Program.ReportsFolder };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        comparison.Clear();
        foreach (string path in picker.FileNames)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                string Value(string key) => root.TryGetProperty(key, out var value) ? value.ToString() : "—";
                comparison.AppendText($"{Path.GetFileName(path)}\r\n  Итог: {Value("outcome")}\r\n  Задержка: {Value("handshake_delay_ms")} мс; ECU: {Value("verified_ecu_values")}\r\n");
                if (root.TryGetProperty("events", out var events))
                {
                    double? handshakeAt = null, requestAt = null, responseAt = null;
                    foreach (var entry in events.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("operation", out var operation) || !entry.TryGetProperty("stage", out var stage)) continue;
                        double ms = entry.GetProperty("elapsed_ms").GetDouble();
                        if (operation.GetString() == "read_completed" && stage.GetString() == "handshake") handshakeAt = ms;
                        if (operation.GetString() == "write_call" && stage.GetString() == "read_bui") requestAt = ms;
                        if (operation.GetString() == "read_completed" && stage.GetString() == "session" && responseAt == null) responseAt = ms;
                    }
                    if (handshakeAt.HasValue && requestAt.HasValue) comparison.AppendText($"  Фактический интервал: {requestAt - handshakeAt:F3} мс\r\n");
                    if (requestAt.HasValue && responseAt.HasValue) comparison.AppendText($"  Первый IN после запроса: {responseAt - requestAt:F3} мс (связь с запросом отдельно не доказана)\r\n");
                }
                comparison.AppendText("\r\n");
            }
            catch (Exception ex) { comparison.AppendText(Path.GetFileName(path) + ": " + ex.Message + "\r\n"); }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (activeRun is { IsCompleted: false }) { cancellation?.Cancel(); e.Cancel = true; state.Text = "Завершаю USB-сеанс и сохраняю отчёт; затем окно можно закрыть."; return; }
        base.OnFormClosing(e);
    }
}
