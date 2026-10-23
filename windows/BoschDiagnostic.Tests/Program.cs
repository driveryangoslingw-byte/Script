using BoschBES2Auto;
using System.Diagnostics;
using System.Text.Json;

// Offline only. No default DiagnosticRunner is constructed; every HID operation uses a fake.
int checks = 0;
void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
var testDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-reports", Guid.NewGuid().ToString("N")));
DiagnosticOptions Options(bool enumerate = false, int delay = 0, string? directory = null) => new() {
    EnumerateOnly = enumerate, HandshakeDelayMs = delay, HandshakeTimeoutMs = 25,
    SessionTimeoutMs = 25, ReportDirectory = directory ?? testDirectory
};
DiagnosticReport Run(FakeStream stream, DiagnosticOptions? options = null, CancellationToken token = default) =>
    new DiagnosticRunner(new FakeBackend(new FakeDevice(stream))).Run(options ?? Options(), token);
byte[] Raw(params byte[] bytes) { var result = new byte[64]; bytes.CopyTo(result, 0); return result; }

var enumerateStream = new FakeStream();
var enumerateDevice = new FakeDevice(enumerateStream);
var enumeration = new DiagnosticRunner(new FakeBackend(enumerateDevice)).Run(Options(enumerate: true));
Assert(enumeration.Outcome == "ENUMERATION_ONLY" && enumerateDevice.Opens == 0 && enumerateStream.Writes.Count == 0, "enumerate must not open or write");
Assert(enumeration.ReportFile is not null && File.Exists(enumeration.ReportFile), "enumeration saves report");
using (var json = JsonDocument.Parse(File.ReadAllText(enumeration.ReportFile!))) {
    Assert(json.RootElement.GetProperty("verified_ecu_values").GetInt32() == 0, "enumeration has zero verified values");
}

var rejection = new FakeStream(Raw(0, 0xFF));
var rejected = Run(rejection);
Assert(rejected.Outcome == "TRANSPORT_ERROR_00_FF" && rejection.Writes.Count == 1 && rejection.Disposed, "handshake 00FF stops and disposes");
Assert(rejected.HandshakeStatus == "TRANSPORT_ERROR_00_FF" && rejected.SessionStatus == "STOPPED_00_FF", "00FF levels retained");

var modeB = new FakeStream(Raw(0, 1, 3));
Assert(Run(modeB).Outcome == "HANDSHAKE_NOT_MODE_A" && modeB.Writes.Count == 1, "mode B never sends ReadBui");

// Hardware-observed header and CAN record; exclude the live RAM tail from
// this public fixture. Synthetic AA bytes verify that padding is not decoded.
var observedBui = new byte[64];
Convert.FromHexString("0200000008980201003D080762022005090200").CopyTo(observedBui, 0);
Array.Fill(observedBui, (byte)0xAA, 19, 45);
var wrongDid = observedBui.ToArray(); wrongDid[14] = 0x21;
var sequence = new FakeStream(Raw(9, 9), Raw(0, 1, 2), Raw(0, 1, 2), Raw(9, 8), wrongDid, Raw(0, 0xFF));
var sequenceReport = Run(sequence);
Assert(sequenceReport.Outcome == "TRANSPORT_ERROR_00_FF" && sequence.Writes.Count == 2, "unknown and ack do not end session before 00FF");
Assert(sequenceReport.VerifiedEcuValues == 0 && sequenceReport.Values.Count == 0, "wrong DID and unknown frames never become ECU values");
Assert(sequence.Writes[0].SequenceEqual(Protocol.ToWindowsReport(Protocol.Handshake(), 65)) &&
    sequence.Writes[1].SequenceEqual(Protocol.ToWindowsReport(Protocol.ReadBui(), 65)), "only exact known two writes");
Assert(sequenceReport.Events.Count(e => e.Operation == "write_call") == 2 &&
    sequenceReport.Events.Count(e => e.Operation == "write_completed") == 2, "write call and completion separately recorded");
Assert(sequenceReport.Events.Where(e => e.Operation == "read_completed").All(e => e.WindowsReportHex!.Split(' ').Length == 65 && e.BoschPayloadHex!.Split(' ').Length == 64), "full Windows65 and Bosch64 preserved");
Assert(sequenceReport.Events.All(e => e.UtcTimestamp.Offset == TimeSpan.Zero) &&
    sequenceReport.Events.Zip(sequenceReport.Events.Skip(1)).All(p => p.First.ElapsedMs <= p.Second.ElapsedMs), "UTC and monotonic timestamps");

var confirmedStream = new FakeStream(Raw(0, 1, 2), observedBui, Raw(0, 0xFF));
var confirmed = Run(confirmedStream);
Assert(confirmed.Outcome == "BUI_SOFTWARE_VERSION_READ" && confirmed.SessionStatus == "READ_RESPONSE_CONFIRMED" &&
    confirmed.EcuStatus == "BUI_READ_CONFIRMED", "real BUI reply confirms precisely the requested software read");
Assert(confirmed.VerifiedEcuValues == 1 && confirmed.Values.Count == 1 && confirmed.Values[0].Ecu == "BUI" &&
    confirmed.Values[0].Cid == "003D" && confirmed.Values[0].Did == "0220" &&
    confirmed.Values[0].Name == "Версия ПО Intuvia" && confirmed.Values[0].Value == "5.9.2.0" &&
    confirmed.Values[0].RawDataHex == "05 09 02 00", "typed value preserves ECU, CID, DID and exact four software bytes");
Assert(confirmedStream.Writes.Count == 2 && confirmed.IncomingReports == 2 && confirmedStream.Disposed,
    "confirmed response closes immediately without further read or write");
using (var json = JsonDocument.Parse(File.ReadAllText(confirmed.ReportFile!))) {
    Assert(json.RootElement.GetProperty("verified_ecu_values").GetInt32() == 1 &&
        json.RootElement.GetProperty("values")[0].GetProperty("raw_data_hex").GetString() == "05 09 02 00", "persisted JSON contains exactly the confirmed value");
}

var prematureStream = new FakeStream(observedBui, Raw(0, 1, 2), Raw(0, 0xFF));
var premature = Run(prematureStream);
Assert(premature.VerifiedEcuValues == 0 && premature.Outcome == "TRANSPORT_ERROR_00_FF", "a valid-looking BUI reply before this request cannot become its value");

var wrongCid = observedBui.ToArray(); wrongCid[9] = 0x3E;
var uncorrelated = Run(new FakeStream(Raw(0, 1, 2), wrongCid, Raw(0, 0xFF)));
Assert(uncorrelated.VerifiedEcuValues == 0, "a different response CID never becomes a BUI value");

var negative = observedBui.ToArray(); negative[10] = 4; negative[11] = 3;
negative[12] = 0x7F; negative[13] = 0x22; negative[14] = 0x31;
var negativeStream = new FakeStream(Raw(0, 1, 2), negative, observedBui);
var negativeReport = Run(negativeStream);
Assert(negativeReport.Outcome == "UDS_NEGATIVE_0x31" && negativeReport.SessionStatus == "READ_REQUEST_REJECTED" &&
    negativeReport.EcuStatus == "BUI_READ_REJECTED" && negativeReport.VerifiedEcuValues == 0, "correlated UDS negative is rejected without values");
Assert(negativeStream.Writes.Count == 2 && negativeReport.IncomingReports == 2 && negativeStream.Disposed,
    "correlated negative stops without consuming a later positive reply");

var unknown = new FakeStream(Raw(9, 9));
Assert(Run(unknown).Outcome == "HANDSHAKE_RESPONSE_TIMEOUT" && unknown.Writes.Count == 1, "unknown handshake cannot authorize ReadBui");

var failWrite = new FakeStream { FailWrite = true };
var failed = Run(failWrite);
Assert(failed.WriteCalls == 1 && failed.CompletedWrites == 0 && failWrite.Disposed, "failed write not counted completed; stream disposed");
Assert(failed.Events.Single(e => e.Operation == "write_failed").DeliveryUnknown && failed.Events.All(e => e.Operation != "write_completed"), "failed delivery stays unknown");

using var cancellation = new CancellationTokenSource();
var cancelledStream = new FakeStream(Raw(0, 1, 2)) { OnRead = () => cancellation.Cancel() };
var cancelled = Run(cancelledStream, token: cancellation.Token);
Assert(cancelled.Outcome == "CANCELLED" && cancelledStream.Writes.Count == 1 && cancelledStream.Disposed, "cancel between handshake and request prevents second write");

var delayRejection = new FakeStream(Raw(0, 1, 2), Raw(0, 0xFF));
var delayReport = Run(delayRejection, Options(delay: 100));
Assert(delayReport.Outcome == "TRANSPORT_ERROR_00_FF" && delayRejection.Writes.Count == 1, "00FF during passive delay prevents ReadBui");

var timedStream = new FakeStream(Raw(0, 1, 2), Raw(0, 1, 2), Raw(8, 8)) { TimeoutBeforeFirstPacket = true };
var timed = Run(timedStream);
Assert(timedStream.Writes.Count == 2 && timed.Outcome == "UNVERIFIED_FRAMES_OBSERVED", "a completed timeout does not consume next queued frame");
Assert(timedStream.MaxReadTimeout <= 200 && timed.Events.Any(e => e.Operation == "read_timeout"), "reads bounded to 200ms");
Assert(timed.VerifiedEcuValues == 0 && timed.UnverifiedCandidateFrames == 0, "ack and unknown are never ECU data");

var continuouslyUnknown = new FakeStream(Raw(0, 1, 2)) { RepeatUnknown = true };
var watch = Stopwatch.StartNew();
var bounded = Run(continuouslyUnknown);
Assert(watch.ElapsedMilliseconds < 500 && continuouslyUnknown.Reads < 100, "incoming unknown frames cannot reset common deadline");
Assert(bounded.Outcome == "UNVERIFIED_FRAMES_OBSERVED" && bounded.VerifiedEcuValues == 0, "continuous unknown remains unverified");

Directory.CreateDirectory(testDirectory);
var occupiedPath = Path.Combine(testDirectory, "occupied-file"); File.WriteAllText(occupiedPath, "test");
var saveFailure = Run(new FakeStream(Raw(0, 0xFF)), Options(directory: occupiedPath));
Assert(saveFailure.Outcome == "TRANSPORT_ERROR_00_FF" && saveFailure.ReportSaveError is not null && saveFailure.ReportFile is null, "report-save error cannot relabel USB result");

var malformed = new FakeStream { MalformedLength = 64 };
var malformedReport = Run(malformed);
Assert(malformedReport.Outcome == "USB_OR_ENUMERATION_ERROR" && malformed.Writes.Count == 1 && malformed.Disposed, "invalid report length cannot authorize a second write");

var wrongProperties = new FakeDevice(new FakeStream()) { InputLength = 64 };
var unsupported = new DiagnosticRunner(new FakeBackend(wrongProperties)).Run(Options());
Assert(unsupported.Outcome == "UNSUPPORTED_DEVICE_PROPERTIES" && wrongProperties.Opens == 0, "wrong report lengths rejected before open");
var ambiguous = new DiagnosticRunner(new FakeBackend(new FakeDevice(new()), new FakeDevice(new()))).Run(Options());
Assert(ambiguous.Outcome == "AMBIGUOUS_DEVICE" && ambiguous.WriteCalls == 0, "multiple devices do not pick an arbitrary interface");

var invalidDelayStream = new FakeStream();
var invalidDelay = Run(invalidDelayStream, Options(delay: 3001));
Assert(invalidDelay.Outcome == "INVALID_OPTIONS" && invalidDelayStream.Writes.Count == 0, "delay outside 0..3000 rejected");

using var lockReady = new ManualResetEventSlim();
using var unlock = new ManualResetEventSlim();
var holder = Task.Run(() => { using var mutex = new Mutex(false, DiagnosticRunner.MutexName); mutex.WaitOne(); lockReady.Set(); unlock.Wait(); mutex.ReleaseMutex(); });
lockReady.Wait();
try {
    var busyDevice = new FakeDevice(new());
    var busy = new DiagnosticRunner(new FakeBackend(busyDevice)).Run(Options());
    Assert(busy.Outcome == "SESSION_ALREADY_RUNNING" && busyDevice.Opens == 0 && busy.WriteCalls == 0, "named mutex blocks parallel new sessions");
} finally { unlock.Set(); holder.GetAwaiter().GetResult(); }

Console.WriteLine($"{checks} offline diagnostic checks PASS; no real HID enumeration, opens or writes.");

sealed class FakeBackend(params FakeDevice[] devices) : IDiagnosticHidBackend {
    public IEnumerable<IDiagnosticHidDevice> Enumerate() => devices;
}
sealed class FakeDevice(FakeStream stream) : IDiagnosticHidDevice {
    public int Opens { get; private set; }
    public int InputLength { get; init; } = 65;
    public DiagnosticDeviceInfo Describe() => new() { DevicePath = "OFFLINE_FAKE", VendorId = Protocol.Vid,
        ProductId = Protocol.Pid, InputReportLength = InputLength, OutputReportLength = 65 };
    public IDiagnosticHidStream Open() { Opens++; return stream; }
}
sealed class FakeStream(params byte[][] packets) : IDiagnosticHidStream {
    readonly Queue<byte[]> packets = new(packets);
    int readTimeout;
    public int ReadTimeout { get => readTimeout; set { readTimeout = value; MaxReadTimeout = Math.Max(MaxReadTimeout, value); } }
    public int MaxReadTimeout { get; private set; }
    public int WriteTimeout { get; set; }
    public bool Disposed { get; private set; }
    public bool FailWrite { get; init; }
    public bool TimeoutBeforeFirstPacket { get; set; }
    public bool RepeatUnknown { get; init; }
    public int? MalformedLength { get; init; }
    public Action? OnRead { get; init; }
    public List<byte[]> Writes { get; } = new();
    public int Reads { get; private set; }
    public int Read(byte[] buffer, int offset, int count) {
        Reads++;
        if (TimeoutBeforeFirstPacket) { TimeoutBeforeFirstPacket = false; throw new TimeoutException("offline first timeout"); }
        if (MalformedLength is int length) return length;
        byte[] raw;
        if (packets.Count > 0) raw = packets.Dequeue();
        else if (RepeatUnknown) { Thread.Sleep(2); raw = new byte[64]; raw[0] = 8; }
        else { Thread.Sleep(ReadTimeout); throw new TimeoutException("offline deadline poll"); }
        var windows = Protocol.ToWindowsReport(raw, 65); windows.CopyTo(buffer, offset);
        OnRead?.Invoke();
        return windows.Length;
    }
    public void Write(byte[] buffer, int offset, int count) {
        Writes.Add(buffer.Skip(offset).Take(count).ToArray());
        if (FailWrite) throw new IOException("offline write failure");
    }
    public void Dispose() => Disposed = true;
}
