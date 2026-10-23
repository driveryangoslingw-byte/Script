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
            int did = 0x0220;
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
                    case "--did" when i + 1 < args.Length:
                        string didText = args[++i].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                        if (!int.TryParse(didText, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out did) || !BuiResearch.IsAllowed(did))
                            throw new ArgumentException("--did: choose a catalogued BUI DID (example: 0220, 0221, 024A).");
                        break;
                    case "--report-dir" when i + 1 < args.Length: directory = Path.GetFullPath(args[++i]); break;
                    case "--help":
                        Console.WriteLine("BoschBES2Auto --enumerate | --probe [--did 0220|0221|...] [--delay-ms 0..3000] [--report-dir PATH]");
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
                    EnumerateOnly = enumerate, HandshakeDelayMs = delay, ResearchDid = did, ReportDirectory = directory
                }, cancel.Token, Console.WriteLine);
                Console.WriteLine($"OUTCOME={result.Outcome}; USB={result.UsbStatus}; HANDSHAKE={result.HandshakeStatus}; VERIFIED_ECU_VALUES={result.VerifiedEcuValues}");
                Console.WriteLine("REPORT_FILE=" + result.ReportFile);
                if (result.ReportSaveError != null) { Console.Error.WriteLine(result.ReportSaveError); return 4; }
                return result.Outcome == "ENUMERATION_ONLY" || result.VerifiedEcuValues > 0 ? 0 : result.Outcome == "CANCELLED" ? 130 : 3;
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
    private readonly TextBox logbox = new(), comparison = new(), decoded = new();
    private readonly DataGridView catalogue = new();
    private readonly NumericUpDown delay = new() { Minimum = 0, Maximum = 3000, Value = 0, Width = 110 };
    private readonly ComboBox didChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
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
        foreach (int height in new[] { 42, 56, 30, 48, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "BOSCH BES2 · USB-ДИАГНОСТИКА", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 19, FontStyle.Bold), ForeColor = Color.LightSkyBlue }, 0, 0);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        Style(start, "ПРОЧИТАТЬ ВЫБРАННЫЙ ПАРАМЕТР", Color.FromArgb(25, 112, 172));
        Style(stop, "СТОП", Color.FromArgb(90, 70, 75));
        Style(reports, "ОТЧЁТЫ", Color.FromArgb(42, 72, 91));
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
        state.Text = "Готов. Выбери параметр BUI ниже. Подтверждены форматы ПО, оборудования, пробега и языка. Один запуск читает один DID.";
        state.Dock = DockStyle.Fill; state.ForeColor = Color.Khaki;
        layout.Controls.Add(state, 0, 3);
        layout.Controls.Add(new Label { Text = "Intuvia 108C:0155 · один handshake и один запрос чтения · без изменения настроек\nПроверяется BUI. Диагностика мотора и аккумулятора пока не реализована.", Dock = DockStyle.Fill }, 0, 4);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var resultsTab = new TabPage("Параметры BUI") { BackColor = BackColor };
        var resultsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        resultsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        resultsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        resultsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settings.Controls.Add(new Label { Text = "Параметр BUI:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
        foreach (var target in BuiResearch.Targets) didChoice.Items.Add(target);
        didChoice.SelectedIndex = 0;
        didChoice.Dock = DockStyle.Fill; settings.Controls.Add(didChoice, 1, 0);
        settings.Controls.Add(new Label { Text = "Задержка, мс:", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, 2, 0);
        delay.Dock = DockStyle.Fill; settings.Controls.Add(delay, 3, 0);
        var catalogueNote = new Label { Text = "Формат: VERIFIED / RAW_ONLY / NOT_TESTED. «Ответ ранее» — история USB-чтений.", Dock = DockStyle.Fill };
        settings.Controls.Add(catalogueNote, 0, 1); settings.SetColumnSpan(catalogueNote, 4);
        resultsLayout.Controls.Add(settings, 0, 0);
        PrepareCatalogue(); resultsLayout.Controls.Add(catalogue, 0, 1);
        PrepareText(decoded); decoded.WordWrap = true;
        decoded.Text = "Декодированный результат появится здесь. Чтение ещё не запускалось.";
        resultsLayout.Controls.Add(decoded, 0, 2);
        resultsTab.Controls.Add(resultsLayout); tabs.TabPages.Add(resultsTab);
        var logTab = new TabPage("HEX и события API") { BackColor = BackColor };
        PrepareText(logbox); logTab.Controls.Add(logbox); tabs.TabPages.Add(logTab);
        var research = new TabPage("Исследование и сравнение") { BackColor = BackColor };
        var researchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        researchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var comparePanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var compare = new Button { Text = "Сравнить JSON…", AutoSize = true };
        compare.Click += (_, _) => CompareReports(); comparePanel.Controls.Add(compare);
        researchLayout.Controls.Add(comparePanel, 0, 0);
        researchLayout.Controls.Add(new Label { Text = "Каждый запуск = новый handshake и ОДИН выбранный DID. RAW_ONLY сохраняет ответ без неподтверждённой расшифровки.\nЗдесь можно сравнить уже сохранённые отчёты. UTC и время от начала сеанса сохраняются в JSON.", Dock = DockStyle.Fill }, 0, 1);
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

    private void PrepareCatalogue()
    {
        catalogue.Dock = DockStyle.Fill; catalogue.ReadOnly = true;
        catalogue.AllowUserToAddRows = false; catalogue.AllowUserToDeleteRows = false;
        catalogue.AllowUserToResizeRows = false; catalogue.RowHeadersVisible = false;
        catalogue.AutoGenerateColumns = false; catalogue.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        catalogue.MultiSelect = false; catalogue.BackgroundColor = Color.FromArgb(8, 22, 35);
        catalogue.EnableHeadersVisualStyles = false;
        catalogue.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        catalogue.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(42, 72, 91);
        catalogue.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        catalogue.DefaultCellStyle.BackColor = Color.FromArgb(15, 26, 40);
        catalogue.DefaultCellStyle.ForeColor = Color.White;
        catalogue.DefaultCellStyle.SelectionBackColor = Color.FromArgb(25, 80, 120);
        catalogue.DefaultCellStyle.SelectionForeColor = Color.White;
        catalogue.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        catalogue.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        foreach (var column in new[] {
            new DataGridViewTextBoxColumn { Name = "did", HeaderText = "DID", Width = 62 },
            new DataGridViewTextBoxColumn { Name = "parameter", HeaderText = "Параметр", Width = 190 },
            new DataGridViewTextBoxColumn { Name = "evidence", HeaderText = "Формат", Width = 102 },
            new DataGridViewTextBoxColumn { Name = "observed", HeaderText = "Ответ ранее", Width = 85 },
            new DataGridViewTextBoxColumn { Name = "value", HeaderText = "В этом окне", Width = 145 },
            new DataGridViewTextBoxColumn { Name = "note", HeaderText = "Основание / ограничение", MinimumWidth = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        }) catalogue.Columns.Add(column);
        foreach (var target in BuiResearch.Targets)
            catalogue.Rows.Add(target.Did.ToString("X4"), target.Label, target.EvidenceStatus,
                target.UsbReplyObserved ? "Да" : "Нет", "—", target.Note);
        catalogue.CellDoubleClick += (_, e) => {
            if (e.RowIndex >= 0 && didChoice.Enabled) {
                string did = (string)catalogue.Rows[e.RowIndex].Cells["did"].Value;
                didChoice.SelectedItem = BuiResearch.Targets.Single(t => t.Did.ToString("X4") == did);
            }
        };
    }

    private void ShowResult(DiagnosticReport result)
    {
        var row = catalogue.Rows.Cast<DataGridViewRow>().Single(r => (string)r.Cells["did"].Value == result.ResearchDid);
        catalogue.ClearSelection(); row.Selected = true; catalogue.FirstDisplayedScrollingRowIndex = row.Index;
        string description;
        if (result.Values.Count > 0) {
            row.Cells["value"].Value = string.Join(", ", result.Values.Select(v => v.Value));
            description = string.Join(Environment.NewLine, result.Values.Select(v => $"DID {v.Did} · {v.Name}: {v.Value}\r\nRAW: {v.RawDataHex}"));
        }
        else if (result.CandidateValues.Count > 0) {
            row.Cells["value"].Value = "RAW: " + string.Join(", ", result.CandidateValues.Select(v => v.RawDataHex));
            description = string.Join(Environment.NewLine, result.CandidateValues.Select(v => $"DID {v.Did} · RAW_ONLY: {v.RawDataHex}\r\n{v.Interpretation}"));
        }
        else {
            row.Cells["value"].Value = result.Outcome;
            description = $"DID {result.ResearchDid}: {result.Outcome}. Значение не получено.";
            if (result.Outcome == "NO_DEVICE") description += "\r\nПосле трёх пассивных поисков HID не найден. Убедись, что Intuvia включена и USB подключён. Handshake и запрос DID не отправлялись; это не отказ ECU.";
        }
        decoded.Text = description + "\r\n" + (result.ReportSaveError is null ? "Отчёт: " + result.ReportFile : "Ошибка сохранения: " + result.ReportSaveError);
    }

    private void AppendLog(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => logbox.AppendText(line + Environment.NewLine)); } catch (InvalidOperationException) { }
    }

    private async void StartRun()
    {
        if (activeRun is { IsCompleted: false }) return;
        start.Enabled = false; stop.Enabled = true; delay.Enabled = false; didChoice.Enabled = false; logbox.Clear();
        cancellation = new CancellationTokenSource();
        state.Text = "Проверяю USB и выполняю один запрос BUI DID " + ((BuiResearch.DidTarget)didChoice.SelectedItem!).Did.ToString("X4") + "…";
        decoded.Text = "Выполняется новое чтение… Предыдущие строки таблицы относятся к своим сеансам.";
        var selected = (BuiResearch.DidTarget)didChoice.SelectedItem!;
        var options = new DiagnosticOptions { HandshakeDelayMs = (int)delay.Value, ResearchDid = selected.Did, ReportDirectory = Program.ReportsFolder };
        try
        {
            var run = Task.Run(() => new DiagnosticRunner().Run(options, cancellation.Token, AppendLog));
            activeRun = run;
            var result = await run;
            ShowResult(result);
            levels.Text = result.VerifiedEcuValues > 0
                ? $"USB: подключён     Транспорт: Sliding Window A     Сессия: ответ подтверждён     BUI: {result.VerifiedEcuValues} значение"
                : result.Outcome == "BUI_READ_CANDIDATE_UNVERIFIED"
                    ? $"USB: подключён     Транспорт: Sliding Window A     DID {result.ResearchDid}: кандидат ответа, значение НЕ проверено"
                    : $"USB: {result.UsbStatus}     Handshake: {result.HandshakeStatus}     Сессия: {result.SessionStatus}     ECU: 0";
            state.Text = result.Outcome switch {
                "BUI_SOFTWARE_VERSION_READ" => "Успешно прочитана версия ПО Intuvia: " + string.Join(", ", result.Values.Select(value => value.Value)) + ". Отчёт сохранён.",
                "BUI_ODOMETER_READ" => "Общий пробег Intuvia: " + string.Join(", ", result.Values.Select(value => value.Value)) + " (DID 0249, данные подтверждены сравнением с дисплеем). Отчёт сохранён.",
                "BUI_HARDWARE_VERSION_READ" => "Версия оборудования Intuvia: " + string.Join(", ", result.Values.Select(value => value.Value)) + " (DID 0221, совпадает с образцом отчёта Bosch). Отчёт сохранён.",
                "BUI_LANGUAGE_READ" => "Язык Intuvia: " + string.Join(", ", result.Values.Select(value => value.Value)) + " (таблица кодов подтверждена прошивкой). Отчёт сохранён.",
                "BUI_READ_CANDIDATE_UNVERIFIED" => "Получен ответ на DID " + result.ResearchDid + ": " +
                    string.Join(", ", result.CandidateValues.Select(v => v.RawDataHex)) +
                    ". " + string.Join(" ", result.CandidateValues.Select(v => v.Interpretation)),
                "TRANSPORT_ERROR_00_FF" => "Получен 00 FF. Значение ошибки не расшифровано; чтение ECU не подтверждено. Сеанс закрыт.",
                "NO_DEVICE" => "Bosch 108C:0155 не найден после трёх пассивных поисков. Handshake и запрос не отправлялись. Проверь включение Intuvia и USB-кабель.",
                "CANCELLED" => "Остановлено. Новые запросы не отправляются.",
                "SESSION_ALREADY_RUNNING" => "Другой экземпляр программы выполняет проверку. Новый сеанс не запускался.",
                "UNVERIFIED_FRAMES_OBSERVED" => "Получены кадры. Корректность диагностического ответа и источник ECU ещё не подтверждены.",
                _ => $"Проверка завершена: {result.Outcome}. Подтверждённых значений ECU: 0."
            };
            if (result.ReportSaveError != null) state.Text += " Ошибка сохранения: " + result.ReportSaveError;
            else AppendLog("Отчёт: " + result.ReportFile);
        }
        catch (Exception ex) { state.Text = "Ошибка выполнения: " + ex.Message; }
        finally { activeRun = null; cancellation.Dispose(); cancellation = null; start.Enabled = true; stop.Enabled = false; delay.Enabled = true; didChoice.Enabled = true; }
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

