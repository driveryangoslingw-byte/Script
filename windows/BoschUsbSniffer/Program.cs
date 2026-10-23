using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace BoschUsbSniffer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SnifferWindow());
    }
}

public sealed class SnifferWindow : Form
{
    private readonly Button start = new(), stop = new(), open = new(), folder = new(), export = new(), install = new();
    private readonly ComboBox hubs = new();
    private readonly CheckBox boschOnly = new();
    private readonly Label status = new();
    private readonly DataGridView grid = new();
    private readonly TextBox details = new();
    private readonly List<Process> processes = new();
    private readonly List<Task> copyTasks = new();
    private readonly List<UsbFrame> frames = new();
    private readonly List<string> pcapPaths = new();
    private string? sessionDir;
    private bool capturing;
    private int countAll;

    private static string BaseFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Bosch-BES2", "Captures");

    public SnifferWindow()
    {
        Text = "Bosch USB Packet Inspector — USBPcap";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(970, 650);
        Size = new Size(1280, 820);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(13, 21, 34);
        ForeColor = Color.White;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 59));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

        var title = new Label {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Text = "USB PACKET INSPECTOR   /   BOSCH BES2",
            ForeColor = Color.FromArgb(114, 210, 250),
            Font = new Font("Segoe UI", 20, FontStyle.Bold)
        };
        panel.Controls.Add(title, 0, 0);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        Setup(start, "▶  НАЧАТЬ ЗАХВАТ", Color.FromArgb(28, 117, 171), 197);
        Setup(stop, "■  СТОП + АНАЛИЗ", Color.FromArgb(165, 73, 77), 187);
        Setup(open, "ОТКРЫТЬ .PCAP", Color.FromArgb(44, 78, 103), 158);
        Setup(export, "ЭКСПОРТ JSON", Color.FromArgb(44, 78, 103), 155);
        Setup(folder, "ПАПКА", Color.FromArgb(44, 78, 103), 105);
        Setup(install, "USBPCAP", Color.FromArgb(67, 80, 89), 113);
        foreach (var b in new[] { start, stop, open, export, folder, install }) toolbar.Controls.Add(b);
        stop.Enabled = false;
        panel.Controls.Add(toolbar, 0, 1);

        var second = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        second.Controls.Add(new Label { Text = "USB-контроллеры:", AutoSize = true, Margin = new Padding(2, 13, 9, 0), ForeColor = Color.LightSteelBlue });
        hubs.DropDownStyle = ComboBoxStyle.DropDownList;
        hubs.Width = 252;
        hubs.Margin = new Padding(0, 7, 16, 0);
        hubs.BackColor = Color.FromArgb(27, 44, 61);
        hubs.ForeColor = Color.White;
        second.Controls.Add(hubs);
        boschOnly.Text = "Показывать только Bosch-кандидаты";
        boschOnly.AutoSize = true;
        boschOnly.Margin = new Padding(2, 11, 18, 0);
        boschOnly.ForeColor = Color.White;
        second.Controls.Add(boschOnly);
        status.Text = "USBPcap нужен для захвата. Программа не отправляет команды велосипеду.";
        status.AutoSize = true;
        status.MaximumSize = new Size(940, 0);
        status.Margin = new Padding(6, 10, 0, 0);
        status.ForeColor = Color.Khaki;
        second.Controls.Add(status);
        panel.Controls.Add(second, 0, 2);

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.FromArgb(15, 30, 46);
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(35, 53, 72);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(15, 30, 46);
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(210, 230, 240);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(25, 91, 129);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(21, 37, 53);
        grid.RowTemplate.Height = 27;
        grid.Columns.Add("when", "Время");
        grid.Columns.Add("hub", "Источник");
        grid.Columns.Add("dev", "USB bus:dev");
        grid.Columns.Add("direction", "Направление");
        grid.Columns.Add("kind", "Тип");
        grid.Columns.Add("size", "Байт");
        grid.Columns.Add("note", "Разбор / Bosch");
        grid.Columns[0].FillWeight = 130;
        grid.Columns[1].FillWeight = 92;
        grid.Columns[2].FillWeight = 75;
        grid.Columns[3].FillWeight = 67;
        grid.Columns[4].FillWeight = 60;
        grid.Columns[5].FillWeight = 43;
        grid.Columns[6].FillWeight = 270;
        panel.Controls.Add(grid, 0, 3);

        details.ReadOnly = true;
        details.Multiline = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.WordWrap = true;
        details.Dock = DockStyle.Fill;
        details.Font = new Font("Consolas", 10);
        details.BackColor = Color.FromArgb(8, 25, 39);
        details.ForeColor = Color.FromArgb(170, 220, 249);
        details.Text = "Выбери USB-пакет для просмотра HEX.\r\n" +
                       "Внимание: при захвате всего USB-контроллера в .pcap могут попасть данные других USB-устройств.";
        panel.Controls.Add(details, 0, 4);
        Controls.Add(panel);

        start.Click += (_, _) => StartCapture();
        stop.Click += async (_, _) => await StopCapture();
        open.Click += (_, _) => OpenCapture();
        folder.Click += (_, _) => OpenFolder();
        export.Click += (_, _) => ExportSummary();
        install.Click += (_, _) => Process.Start(new ProcessStartInfo("https://desowin.org/usbpcap/") { UseShellExecute = true });
        boschOnly.CheckedChanged += (_, _) => Render();
        grid.SelectionChanged += (_, _) => ShowSelection();
        Shown += (_, _) => DetectHubs();
    }

    private static void Setup(Button b, string text, Color color, int width)
    {
        b.Text = text;
        b.Width = width; b.Height = 40; b.Margin = new Padding(3);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = color; b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 9, FontStyle.Bold);
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        status.Text = text;
    }

    private void DetectHubs()
    {
        hubs.Items.Clear();
        var detected = UsbPcapDevices.Find();
        hubs.Items.Add("AUTO — все найденные (" + detected.Count + ")");
        foreach (var h in detected) hubs.Items.Add(h);
        hubs.SelectedIndex = 0;
        if (detected.Count == 0)
            SetStatus("USBPcap-фильтры не найдены. Установи USBPcap, перезагрузи Windows и открой программу заново.");
        else
            SetStatus("Готово: " + detected.Count + " USBPcap контроллер(ов). Для записи нажми «НАЧАТЬ ЗАХВАТ».");
    }

    private static string? FindUsbPcapExe()
    {
        string[] paths = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(AppContext.BaseDirectory, "USBPcapCMD.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void StartCapture()
    {
        if (capturing) return;
        var exe = FindUsbPcapExe();
        if (exe == null)
        {
            MessageBox.Show("USBPcapCMD.exe не найден. Установи официальный USBPcap, перезагрузи компьютер и нажми запуск снова.\n" +
                            "https://desowin.org/usbpcap/", "Нужен USBPcap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Elevated())
        {
            MessageBox.Show("Для доступа к драйверу USBPcap запусти эту программу от имени администратора.\n" +
                            "Программа не изменяет USB-драйвер велосипеда.", "Нужен администратор", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var all = UsbPcapDevices.Find();
        var selected = hubs.SelectedIndex > 0 ? new[] { hubs.SelectedItem!.ToString()! } : all.ToArray();
        if (selected.Length == 0)
        {
            DetectHubs();
            MessageBox.Show("Нет доступных USBPcap-контроллеров. Установи USBPcap и перезагрузи Windows.", "USBPcap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show("Будет записываться USB-трафик выбранных контроллеров, в том числе возможные данные других USB-устройств.\n" +
            "Не публикуй исходный .pcap без проверки. Продолжить?", "Подтверждение захвата USB",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        sessionDir = Path.Combine(BaseFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(sessionDir);
        frames.Clear(); pcapPaths.Clear(); countAll = 0;
        processes.Clear(); copyTasks.Clear();
        foreach (var hub in selected)
        {
            string basename = hub.Split('\\').Last();
            string outFile = Path.Combine(sessionDir, basename + ".pcap");
            try
            {
                var psi = new ProcessStartInfo(exe) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(hub);
                psi.ArgumentList.Add("-A");
                psi.ArgumentList.Add("--inject-descriptors");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("-");
                var process = Process.Start(psi) ?? throw new IOException("USBPcapCMD did not launch.");
                processes.Add(process);
                pcapPaths.Add(outFile);
                copyTasks.Add(CopyPcap(process, outFile));
                _ = Task.Run(async () => {
                    try {
                        string stderr = await process.StandardError.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(stderr) && stderr.Contains("error", StringComparison.OrdinalIgnoreCase))
                            SetStatus("USBPcap: " + stderr.Trim().Replace('\n', ' ').Take(170).Aggregate("", (a, c) => a + c));
                    } catch { }
                });
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка запуска " + hub + ": " + ex.Message);
            }
        }
        if (processes.Count == 0) return;
        capturing = true;
        start.Enabled = false;
        stop.Enabled = true;
        open.Enabled = false;
        SetStatus("ЗАХВАТ ИДЁТ. Теперь выполни диагностическое действие в рабочем Bosch-приложении. Затем нажми СТОП + АНАЛИЗ.");
    }

    private async Task CopyPcap(Process process, string path)
    {
        try
        {
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, useAsync: true);
            var source = process.StandardOutput.BaseStream;
            byte[] buffer = new byte[65536];
            while (true)
            {
                int read = await source.ReadAsync(buffer);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read));
                await target.FlushAsync();
            }
        }
        catch (IOException ex) { SetStatus("Поток захвата прерван: " + ex.Message); }
        catch (Exception ex) { SetStatus("Ошибка USBPcap: " + ex.Message); }
    }

    private async Task StopCapture()
    {
        if (!capturing) return;
        capturing = false;
        stop.Enabled = false;
        SetStatus("Останавливаю захват и читаю файлы...");
        foreach (var p in processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { }
        }
        try { await Task.WhenAll(copyTasks).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { SetStatus("Завершение потоков: " + ex.Message); }
        foreach (var p in processes) p.Dispose();
        processes.Clear(); copyTasks.Clear();
        open.Enabled = true;
        start.Enabled = true;
        await LoadPcaps(pcapPaths.ToList());
    }

    private async Task LoadPcaps(IReadOnlyList<string> paths)
    {
        var filePaths = paths.Where(path => File.Exists(path) && new FileInfo(path).Length >= 24).ToList();
        if (filePaths.Count == 0)
        {
            SetStatus("Нет корректных .pcap. Проверь установку USBPcap/права администратора; попробуй другой USB-порт.");
            return;
        }
        try
        {
            var summaries = await Task.Run(() => filePaths.Select(p => PcapDecoder.Read(p)).ToList());
            frames.Clear();
            foreach (var s in summaries) frames.AddRange(s.Frames);
            frames.Sort((a, b) => a.At.CompareTo(b.At));
            countAll = summaries.Sum(s => s.Records);
            Render();
            int hits = frames.Count(f => f.BoschCandidate);
            SetStatus($"PCAP: {filePaths.Count} файл(ов), {countAll} записей, {hits} Bosch-кандидатов. " +
                      "USB-фреймы сохранены; протокол ECU не считается расшифрованным.");
            if (summaries.Any(s => s.PartialTail))
                details.Text = "Предупреждение: последняя запись PCAP не завершилась при остановке. Остальные пакеты сохранены.";
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка PCAP: " + ex.Message);
        }
    }

    private void Render()
    {
        grid.Rows.Clear();
        var selected = boschOnly.Checked ? frames.Where(f => f.BoschCandidate) : frames.AsEnumerable();
        int added = 0;
        foreach (var f in selected)
        {
            if (added >= 7500) break; // bounded UI; complete data remains in original .pcap
            int i = grid.Rows.Add(f.At.ToLocalTime().ToString("HH:mm:ss.ffffff"),
                f.Source, f.Bus + ":" + f.Device, f.Direction, f.TransferName, f.Data.Length,
                f.Note);
            grid.Rows[i].Tag = f;
            if (f.BoschCandidate) grid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(111, 224, 179);
            added++;
        }
        if (grid.Rows.Count > 0) { grid.ClearSelection(); grid.CurrentCell = null; }
        details.Text = $"Всего записей: {countAll}. В таблице: {added}. " +
                       "Кликни по строке для HEX. Полные пакеты находятся в .pcap.";
    }

    private void ShowSelection()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not UsbFrame f) return;
        var sb = new StringBuilder();
        sb.AppendLine($"Источник: {f.Source}  /  время: {f.At:O}");
        sb.AppendLine($"USB: bus={f.Bus} device={f.Device} endpoint=0x{f.Endpoint:X2} ({f.Direction})");
        sb.AppendLine($"Transfer={f.TransferName}  info=0x{f.Info:X2}  status=0x{f.Status:X8}");
        sb.AppendLine($"USBPcap header={f.HeaderLength} B; payload={f.Data.Length} B");
        sb.AppendLine($"Классификация: {f.Note}");
        sb.AppendLine();
        sb.AppendLine(f.Hex);
        details.Text = sb.ToString();
    }

    private void OpenCapture()
    {
        if (capturing) return;
        using var chooser = new OpenFileDialog {
            Filter = "USBPcap classic PCAP (*.pcap)|*.pcap|All files (*.*)|*.*",
            Multiselect = true, Title = "Открыть USBPcap .pcap"
        };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        sessionDir = Path.GetDirectoryName(chooser.FileNames[0]);
        pcapPaths.Clear(); pcapPaths.AddRange(chooser.FileNames);
        _ = LoadPcaps(chooser.FileNames);
    }

    private void ExportSummary()
    {
        if (frames.Count == 0) { MessageBox.Show("Сначала открой или запиши .pcap."); return; }
        string directory = sessionDir ?? BaseFolder;
        Directory.CreateDirectory(directory);
        string name = Path.Combine(directory, "bosch-usb-analysis-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        var matches = frames.Where(f => f.BoschCandidate).Take(1500).Select(f => new {
            source = f.Source, timestamp = f.At, f.Bus, f.Device,
            endpoint = $"0x{f.Endpoint:X2}", direction = f.Direction, transfer = f.TransferName,
            note = f.Note, hex = f.Hex
        });
        var doc = new {
            application = "Bosch USB Packet Inspector 1.0",
            analyzed_utc = DateTimeOffset.UtcNow,
            usbpcap_linktype = 249,
            pcap_files = pcapPaths.Select(Path.GetFileName).ToArray(),
            total_usb_records = countAll,
            bosch_candidate_count = frames.Count(f => f.BoschCandidate),
            warning = "Contains raw USB packets which may include serial identifiers. Not proof of authenticated ECU session. Inspect and redact before sharing.",
            candidates = matches
        };
        File.WriteAllText(name, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus("JSON сохранён: " + name);
    }

    private void OpenFolder()
    {
        string path = sessionDir ?? BaseFolder;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var p in processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
        base.OnFormClosing(e);
    }
}

internal static class UsbPcapDevices
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    public static List<string> Find()
    {
        var detected = new List<string>();
        for (int i = 1; i <= 24; i++)
        {
            string name = @"\\.\" + "USBPcap" + i;
            using var handle = CreateFile(name, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid || Marshal.GetLastWin32Error() == 5)
                detected.Add(name);
        }
        return detected;
    }
}
