using HidSharp;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoschBES2Auto;

public sealed class DiagnosticOptions
{
    public bool EnumerateOnly { get; init; }
    public int HandshakeDelayMs { get; init; } = 0;
    public int HandshakeTimeoutMs { get; init; } = 2500;
    public int SessionTimeoutMs { get; init; } = 3000;
    public string ReportDirectory { get; init; } = "";
}

public sealed class DiagnosticDeviceInfo
{
    public string DevicePath { get; init; } = "";
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }
    public string? PropertyError { get; init; }
}

public sealed class DiagnosticEvent
{
    public DateTimeOffset UtcTimestamp { get; init; }
    public double ElapsedMs { get; init; }
    public string Operation { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public int? ByteCount { get; init; }
    public string? WindowsReportHex { get; init; }
    public string? BoschPayloadHex { get; init; }
    public bool? Success { get; init; }
    public bool DeliveryUnknown { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record DiagnosticValue
{
    public string Ecu { get; init; } = "BUI";
    public string Cid { get; init; } = "003D";
    public string Did { get; init; } = "0220";
    public string Name { get; init; } = "Версия ПО Intuvia";
    public string Value { get; init; } = "";
    public string RawDataHex { get; init; } = "";
}

public sealed class DiagnosticReport
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedUtc { get; set; }
    public double DurationMs { get; set; }
    public bool EnumerateOnly { get; init; }
    public int HandshakeDelayMs { get; init; }
    public string Outcome { get; set; } = "INCOMPLETE";
    public string UsbStatus { get; set; } = "NOT_ENUMERATED";
    public string HandshakeStatus { get; set; } = "NOT_SENT";
    public string SessionStatus { get; set; } = "NOT_STARTED";
    public string EcuStatus { get; set; } = "NO_VERIFIED_ECU_DATA";
    // Only a validated response to this session's single BUI DID 0220 request adds a value.
    public int VerifiedEcuValues => Values.Count;
    public int WriteCalls { get; set; }
    public int CompletedWrites { get; set; }
    public int IncomingReports { get; set; }
    public int UnverifiedCandidateFrames { get; set; }
    public bool StreamClosed { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReportFile { get; set; }
    public string? ReportSaveError { get; set; }
    public List<DiagnosticDeviceInfo> Devices { get; } = new();
    public List<DiagnosticValue> Values { get; } = new();
    public List<DiagnosticEvent> Events { get; } = new();
}

/// <summary>
/// One explicitly requested session: one known handshake and at most one ReadBui request.
/// Does not implement, infer, or repair Sliding Window state, routing, or heartbeat.
/// Run is synchronous so its named mutex is acquired and released on the same thread.
/// </summary>
public sealed class DiagnosticRunner
{
    internal const string MutexName = @"Local\BoschBES2Auto_108C_0155";
    private readonly IDiagnosticHidBackend backend;

    public DiagnosticRunner() : this(new HidSharpBackend()) { }
    internal DiagnosticRunner(IDiagnosticHidBackend backend) => this.backend = backend;

    public DiagnosticReport Run(DiagnosticOptions options, CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var report = new DiagnosticReport { EnumerateOnly = options.EnumerateOnly,
            HandshakeDelayMs = options.HandshakeDelayMs };
        var clock = Stopwatch.StartNew();
        bool optionsValidated = false;

        void Record(string operation, string stage, string message = "", byte[]? windows = null,
            byte[]? bosch = null, bool? success = null, Exception? error = null, bool deliveryUnknown = false,
            DateTimeOffset? capturedUtc = null, double? capturedElapsedMs = null)
        {
            var entry = new DiagnosticEvent {
                UtcTimestamp = capturedUtc ?? DateTimeOffset.UtcNow, ElapsedMs = capturedElapsedMs ?? clock.Elapsed.TotalMilliseconds,
                Operation = operation, Stage = stage, Message = message,
                ByteCount = windows?.Length, WindowsReportHex = windows is null ? null : Protocol.Hex(windows),
                BoschPayloadHex = bosch is null ? null : Protocol.Hex(bosch), Success = success,
                ErrorType = error?.GetType().FullName, ErrorMessage = error?.Message,
                DeliveryUnknown = deliveryUnknown
            };
            report.Events.Add(entry);
            // An observer must not disrupt USB cleanup or turn a report write into a USB error.
            try { log?.Invoke($"{entry.UtcTimestamp:O} +{entry.ElapsedMs:F3}ms {operation}/{stage}: {message}" +
                (windows is null ? "" : $" WINDOWS{windows.Length}=[{entry.WindowsReportHex}]") +
                (bosch is null ? "" : $" BOSCH{bosch.Length}=[{entry.BoschPayloadHex}]") +
                (error is null ? "" : $" {entry.ErrorType}: {entry.ErrorMessage}")); } catch { }
        }

        bool TransportError(byte[] raw, string stage)
        {
            if (raw.Length != Protocol.Len || raw[0] != 0 || raw[1] != 0xFF) return false;
            report.Outcome = "TRANSPORT_ERROR_00_FF";
            if (stage == "handshake") report.HandshakeStatus = "TRANSPORT_ERROR_00_FF";
            report.SessionStatus = "STOPPED_00_FF";
            Record("stop", stage, "Observed 00 FF; meaning undecoded. No further writes.");
            return true;
        }

        void Write(IDiagnosticHidStream stream, byte[] raw, string stage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = Protocol.ToWindowsReport(raw, 65);
            stream.WriteTimeout = 200;
            report.WriteCalls++;
            // Capture boundaries beside the native call; hex formatting and log observers run
            // afterwards so their latency cannot be mistaken for transport latency.
            var callUtc = DateTimeOffset.UtcNow;
            var callElapsedMs = clock.Elapsed.TotalMilliseconds;
            try
            {
                stream.Write(windows, 0, windows.Length);
                var completedUtc = DateTimeOffset.UtcNow;
                var completedElapsedMs = clock.Elapsed.TotalMilliseconds;
                report.CompletedWrites++;
                Record("write_call", stage, "Attempted bytes; not proof of delivery.", windows, raw,
                    capturedUtc: callUtc, capturedElapsedMs: callElapsedMs);
                Record("write_completed", stage, "HidStream.Write returned; ECU acceptance unverified.", windows, raw, true,
                    capturedUtc: completedUtc, capturedElapsedMs: completedElapsedMs);
            }
            catch (Exception ex)
            {
                var failedUtc = DateTimeOffset.UtcNow;
                var failedElapsedMs = clock.Elapsed.TotalMilliseconds;
                Record("write_call", stage, "Attempted bytes; not proof of delivery.", windows, raw,
                    capturedUtc: callUtc, capturedElapsedMs: callElapsedMs);
                Record("write_failed", stage, "Delivery unknown. No retry is permitted.", windows, raw, false, ex, true,
                    capturedUtc: failedUtc, capturedElapsedMs: failedElapsedMs);
                throw;
            }
        }

        byte[]? Read(IDiagnosticHidStream stream, string stage, double deadlineMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadlineMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return null;
            // Complete each synchronous read before starting another. No abandoned asynchronous
            // reads can consume a later report after the polling timeout or cancellation.
            stream.ReadTimeout = Math.Clamp((int)Math.Ceiling(remaining), 1, 200);
            var windows = new byte[65];
            string readDescription = $"ReadTimeout={stream.ReadTimeout}ms; shared stage deadline.";
            var callUtc = DateTimeOffset.UtcNow;
            var callElapsedMs = clock.Elapsed.TotalMilliseconds;
            int count;
            DateTimeOffset completedUtc;
            double completedElapsedMs;
            try
            {
                count = stream.Read(windows, 0, windows.Length);
                completedUtc = DateTimeOffset.UtcNow;
                completedElapsedMs = clock.Elapsed.TotalMilliseconds;
            }
            catch (TimeoutException ex)
            {
                var timeoutUtc = DateTimeOffset.UtcNow;
                var timeoutElapsedMs = clock.Elapsed.TotalMilliseconds;
                Record("read_call", stage, readDescription, capturedUtc: callUtc, capturedElapsedMs: callElapsedMs);
                Record("read_timeout", stage, "Read completed with timeout; no overlapping read remains.", success: false, error: ex,
                    capturedUtc: timeoutUtc, capturedElapsedMs: timeoutElapsedMs);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception ex)
            {
                var failedUtc = DateTimeOffset.UtcNow;
                var failedElapsedMs = clock.Elapsed.TotalMilliseconds;
                Record("read_call", stage, readDescription, capturedUtc: callUtc, capturedElapsedMs: callElapsedMs);
                Record("read_failed", stage, "No retry or further writes.", success: false, error: ex,
                    capturedUtc: failedUtc, capturedElapsedMs: failedElapsedMs);
                throw;
            }
            var actual = windows.Take(Math.Clamp(count, 0, windows.Length)).ToArray();
            var raw = count == 65 && windows[0] == 0 ? windows.Skip(1).ToArray() : null;
            report.IncomingReports++;
            Record("read_call", stage, readDescription, capturedUtc: callUtc, capturedElapsedMs: callElapsedMs);
            Record("read_completed", stage, $"Read returned {count} bytes.", actual, raw, true,
                capturedUtc: completedUtc, capturedElapsedMs: completedElapsedMs);
            if (count != 65 || windows[0] != 0)
                throw new InvalidDataException($"Expected 65-byte Windows report with Report ID 0, got length={count}, ID={(count > 0 ? windows[0] : -1)}.");
            return raw;
        }

        void Session(IDiagnosticHidStream stream)
        {
            report.HandshakeStatus = "WRITE_ATTEMPTED";
            Write(stream, Protocol.Handshake(), "handshake");
            report.HandshakeStatus = "SENT_AWAITING_REPLY";
            var handshakeDeadline = clock.Elapsed.TotalMilliseconds + options.HandshakeTimeoutMs;
            bool modeA = false;
            while (clock.Elapsed.TotalMilliseconds < handshakeDeadline)
            {
                var raw = Read(stream, "handshake", handshakeDeadline);
                if (raw is null) continue;
                if (TransportError(raw, "handshake")) return;
                if (raw[0] == 0 && raw[1] == 1)
                {
                    report.HandshakeStatus = Protocol.HandshakeResult(raw);
                    Record("classify", "handshake", report.HandshakeStatus);
                    if (raw[2] != 2)
                    {
                        report.Outcome = "HANDSHAKE_NOT_MODE_A";
                        return;
                    }
                    modeA = true;
                    if (raw.Skip(3).Any(b => b != 0))
                        Record("observation", "handshake", "Mode byte is 02; nonzero trailing bytes are preserved and not decoded.");
                    break;
                }
                Record("classify", "handshake", "UNKNOWN_FRAME_NO_ECU_DATA; awaiting the handshake within the same deadline.");
            }
            if (!modeA)
            {
                report.HandshakeStatus = "RESPONSE_DEADLINE_EXPIRED";
                report.Outcome = "HANDSHAKE_RESPONSE_TIMEOUT";
                return;
            }

            report.SessionStatus = "DELAY_BEFORE_REQUEST";
            var delayDeadline = clock.Elapsed.TotalMilliseconds + options.HandshakeDelayMs;
            Record("delay_begin", "session", $"Configured delay={options.HandshakeDelayMs}ms. Passive reads only during delay.");
            while (clock.Elapsed.TotalMilliseconds < delayDeadline)
            {
                var raw = Read(stream, "delay", delayDeadline);
                if (raw is null) continue;
                if (TransportError(raw, "delay")) return;
                Record("classify", "delay", "Unsolicited frame preserved; no ECU values inferred.");
            }
            Record("delay_completed", "session");
            report.SessionStatus = "READ_REQUEST_WRITE_ATTEMPTED";
            Write(stream, Protocol.ReadBui(), "read_bui");
            report.SessionStatus = "READ_REQUEST_SENT_AWAITING_REPLIES";
            var sessionDeadline = clock.Elapsed.TotalMilliseconds + options.SessionTimeoutMs;
            int responseCount = 0;
            while (clock.Elapsed.TotalMilliseconds < sessionDeadline)
            {
                var raw = Read(stream, "session", sessionDeadline);
                if (raw is null) continue;
                responseCount++;
                if (TransportError(raw, "session")) return;
                var classification = Protocol.ReadResult(raw);
                if (Protocol.TryReadBuiSoftwareVersion(raw, out var version))
                {
                    report.Values.Add(new DiagnosticValue { Value = version,
                        RawDataHex = Protocol.Hex(raw.Skip(15).Take(4).ToArray()) });
                    report.Outcome = "BUI_SOFTWARE_VERSION_READ";
                    report.SessionStatus = "READ_RESPONSE_CONFIRMED";
                    report.EcuStatus = "BUI_READ_CONFIRMED";
                    Record("classify", "session", $"BUI_SOFTWARE_VERSION_READ; verified_ecu_values={report.VerifiedEcuValues}. No further writes.");
                    return;
                }
                if (classification.StartsWith("UDS_NEGATIVE_0x", StringComparison.Ordinal))
                {
                    report.Outcome = classification;
                    report.SessionStatus = "READ_REQUEST_REJECTED";
                    report.EcuStatus = "BUI_READ_REJECTED";
                    Record("classify", "session", $"{classification}; verified_ecu_values={report.VerifiedEcuValues}. No further writes.");
                    return;
                }
                if (classification.Contains("CANDIDATE", StringComparison.Ordinal)) report.UnverifiedCandidateFrames++;
                Record("classify", "session", $"{classification}; verified_ecu_values={report.VerifiedEcuValues}. Continue until the same deadline.");
            }
            report.SessionStatus = responseCount == 0 ? "RESPONSE_DEADLINE_EXPIRED" : "DEADLINE_ENDED_WITH_UNVERIFIED_FRAMES";
            report.Outcome = responseCount == 0 ? "NO_SESSION_RESPONSE" : "UNVERIFIED_FRAMES_OBSERVED";
        }

        try
        {
            if (string.IsNullOrWhiteSpace(options.ReportDirectory)) throw new ArgumentException("ReportDirectory must be explicit.");
            if (options.HandshakeDelayMs is < 0 or > 3000) throw new ArgumentOutOfRangeException(nameof(options.HandshakeDelayMs), "Expected 0..3000 ms.");
            if (options.HandshakeTimeoutMs is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(options.HandshakeTimeoutMs));
            if (options.SessionTimeoutMs is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(options.SessionTimeoutMs));
            optionsValidated = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (options.EnumerateOnly)
            {
                // Enumeration never opens a HID stream and therefore cannot send a report.
                foreach (var device in backend.Enumerate()) report.Devices.Add(device.Describe());
                report.UsbStatus = "ENUMERATED_WITHOUT_OPEN";
                report.Outcome = "ENUMERATION_ONLY";
                Record("enumerate_completed", "usb", $"Matching VID/PID interfaces={report.Devices.Count}; zero opens, zero writes.");
            }
            else
            {
                using var mutex = new Mutex(false, MutexName);
                bool ownsMutex = false;
                try
                {
                    try { ownsMutex = mutex.WaitOne(0); }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                        report.Outcome = "PREVIOUS_SESSION_ABANDONED";
                        report.SessionStatus = "BLOCKED_UNKNOWN_PREVIOUS_STATE";
                        Record("stop", "mutex", "Previous process abandoned the mutex; no USB open or writes.");
                    }
                    if (!ownsMutex)
                    {
                        report.Outcome = "SESSION_ALREADY_RUNNING";
                        report.SessionStatus = "BLOCKED_BY_NAMED_MUTEX";
                        Record("stop", "mutex", "Another diagnostic process owns the session; no USB open or writes.");
                    }
                    else if (report.Outcome != "PREVIOUS_SESSION_ABANDONED")
                    {
                        Record("mutex_acquired", "usb", MutexName);
                        var devices = backend.Enumerate().ToArray();
                        foreach (var device in devices) report.Devices.Add(device.Describe());
                        report.UsbStatus = "ENUMERATED";
                        if (devices.Length == 0) { report.Outcome = "NO_DEVICE"; report.UsbStatus = "DEVICE_NOT_FOUND"; }
                        else if (devices.Length != 1) { report.Outcome = "AMBIGUOUS_DEVICE"; report.UsbStatus = "MULTIPLE_MATCHING_INTERFACES"; }
                        else
                        {
                            var info = report.Devices[0];
                            if (info.PropertyError is not null || info.VendorId != Protocol.Vid || info.ProductId != Protocol.Pid ||
                                info.InputReportLength != 65 || info.OutputReportLength != 65)
                            {
                                report.Outcome = "UNSUPPORTED_DEVICE_PROPERTIES";
                                report.UsbStatus = "REQUIRED_108C_0155_IN65_OUT65_NOT_CONFIRMED";
                            }
                            else
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                Record("open_call", "usb", info.DevicePath);
                                report.UsbStatus = "OPEN_ATTEMPTED";
                                var stream = devices[0].Open();
                                report.UsbStatus = "OPEN";
                                Record("open_completed", "usb", "Strict VID/PID and report lengths confirmed.");
                                try { Session(stream); }
                                finally
                                {
                                    Record("dispose_call", "usb");
                                    try { stream.Dispose(); report.StreamClosed = true; Record("dispose_completed", "usb", success: true); }
                                    catch (Exception ex)
                                    {
                                        report.UsbStatus = "CLOSE_ERROR";
                                        Record("dispose_failed", "usb", error: ex, success: false);
                                    }
                                }
                            }
                        }
                    }
                }
                finally { if (ownsMutex) mutex.ReleaseMutex(); }
            }
        }
        catch (OperationCanceledException)
        {
            report.Outcome = "CANCELLED";
            report.SessionStatus = "CANCELLED_NO_FURTHER_WRITES";
            Record("cancelled", "session", "Cancellation observed; no further writes.");
        }
        catch (ArgumentException ex) when (!optionsValidated)
        {
            report.Outcome = "INVALID_OPTIONS";
            report.ErrorType = ex.GetType().FullName; report.ErrorMessage = ex.Message;
            Record("configuration_error", "configuration", error: ex, success: false);
        }
        catch (Exception ex)
        {
            report.Outcome = "USB_OR_ENUMERATION_ERROR";
            report.UsbStatus = "ERROR";
            report.ErrorType = ex.GetType().FullName; report.ErrorMessage = ex.Message;
            Record("error", "usb", "Session stopped. No retry or further writes.", error: ex, success: false);
        }
        report.FinishedUtc = DateTimeOffset.UtcNow;
        report.DurationMs = clock.Elapsed.TotalMilliseconds;
        Record("session_finished", "summary", $"{report.Outcome}; verified_ecu_values={report.VerifiedEcuValues}.");

        // File failures are a separate reporting layer; they must never relabel a USB outcome.
        try
        {
            if (string.IsNullOrWhiteSpace(options.ReportDirectory)) throw new ArgumentException("ReportDirectory must be explicit.");
            Directory.CreateDirectory(options.ReportDirectory);
            report.ReportFile = Path.GetFullPath(Path.Combine(options.ReportDirectory,
                $"bosch-bes2-{report.StartedUtc:yyyyMMdd-HHmmss-fff}-{report.RunId}.json"));
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(report.ReportFile, json, new UTF8Encoding(false));
            try { log?.Invoke($"{DateTimeOffset.UtcNow:O} +{clock.Elapsed.TotalMilliseconds:F3}ms report_saved: {report.ReportFile}"); } catch { }
        }
        catch (Exception ex)
        {
            report.ReportFile = null;
            report.ReportSaveError = $"{ex.GetType().FullName}: {ex.Message}";
            Record("report_save_failed", "report", "USB outcome preserved.", error: ex, success: false);
        }
        return report;
    }
}

// Internal transport seam is for deterministic offline tests. Production always uses HidSharp.
internal interface IDiagnosticHidBackend { IEnumerable<IDiagnosticHidDevice> Enumerate(); }
internal interface IDiagnosticHidDevice { DiagnosticDeviceInfo Describe(); IDiagnosticHidStream Open(); }
internal interface IDiagnosticHidStream : IDisposable
{
    int ReadTimeout { get; set; }
    int WriteTimeout { get; set; }
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}
internal sealed class HidSharpBackend : IDiagnosticHidBackend
{
    public IEnumerable<IDiagnosticHidDevice> Enumerate() => DeviceList.Local.GetHidDevices(Protocol.Vid, Protocol.Pid)
        .Select(device => (IDiagnosticHidDevice)new HidSharpDevice(device));
}
internal sealed class HidSharpDevice(HidDevice device) : IDiagnosticHidDevice
{
    public DiagnosticDeviceInfo Describe()
    {
        try
        {
            // Do not request a serial string, manufacturer/product strings, or feature reports.
            return new DiagnosticDeviceInfo { DevicePath = device.DevicePath, VendorId = device.VendorID,
                ProductId = device.ProductID, InputReportLength = device.GetMaxInputReportLength(),
                OutputReportLength = device.GetMaxOutputReportLength(), FeatureReportLength = device.GetMaxFeatureReportLength() };
        }
        catch (Exception ex)
        {
            return new DiagnosticDeviceInfo { DevicePath = device.DevicePath, VendorId = device.VendorID,
                ProductId = device.ProductID, PropertyError = $"{ex.GetType().FullName}: {ex.Message}" };
        }
    }
    public IDiagnosticHidStream Open() => new HidSharpStream(device.Open());
}
internal sealed class HidSharpStream(HidStream stream) : IDiagnosticHidStream
{
    public int ReadTimeout { get => stream.ReadTimeout; set => stream.ReadTimeout = value; }
    public int WriteTimeout { get => stream.WriteTimeout; set => stream.WriteTimeout = value; }
    public int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
    public void Dispose() => stream.Dispose();
}
