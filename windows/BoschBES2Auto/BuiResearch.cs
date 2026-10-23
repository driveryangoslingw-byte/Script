namespace BoschBES2Auto;

/// <summary>
/// A bounded list of publicly described BUI ReadDataByIdentifier candidates.
/// 0220 and 0249 are hardware-verified, and 0221 matches a published Bosch DiagnosticTool hardware-version example.
/// Each run starts from a new handshake and sends exactly one read request.
/// </summary>
public static class BuiResearch
{
    public sealed record DidTarget(ushort Did, string Label, bool HardwareConfirmed)
    {
        public override string ToString() => $"{Did:X4} · {Label}" + (HardwareConfirmed ? " [проверено]" : " [не проверено]");
    }

    public static readonly IReadOnlyList<DidTarget> Targets = new[] {
        new DidTarget(0x0220, "Версия ПО Intuvia", true),
        new DidTarget(0x0221, "Версия аппаратного обеспечения Intuvia", true),
        new DidTarget(0x0222, "Дата производства", false),
        new DidTarget(0x0249, "Общий пробег Intuvia (метры → км)", true),
        new DidTarget(0x024A, "Язык интерфейса", false),
        new DidTarget(0x024B, "Единицы расстояния", false),
        new DidTarget(0x024C, "Яркость экрана днём", false),
        new DidTarget(0x0250, "Яркость экрана ночью", false),
        new DidTarget(0x0251, "Контрастность дисплея", false),
    };

    /// <summary>
    /// On this bike, DID 0249 returned 00 23 80 5E (2,326,622).
    /// The Intuvia displayed 2326.6 km; the data fits an unsigned
    /// big-endian meter counter / 1000, rounded to one decimal.
    /// This has been cross-checked with one owner-provided reading.
    /// </summary>
    public static bool TryReadOdometer(byte[]? raw, out uint metres, out string kilometres)
    {
        metres = 0;
        kilometres = "";
        if (!TryPositiveRead(raw, 0x0249, out var data) || data.Length != 4) return false;
        metres = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
        kilometres = (metres / 1000m).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// DID 0221 raw 00 00 02 02 was observed repeatedly on this bicycle.
    /// Bosch DiagnosticTool manual (IBD v7.1+) page 26 independently shows
    /// hardware version 0.0.2.2 for a BUI255/Intuvia with SW 5.9.2.0.
    /// Only single-frame positive UDS 62 02 21 with exactly four bytes
    /// is interpreted as dotted decimal version (no writing).
    /// </summary>
    public static bool TryReadHardwareVersion(byte[]? raw, out string version)
    {
        version = "";
        if (!TryPositiveRead(raw, 0x0221, out var data) || data.Length != 4) return false;
        version = $"{data[0]}.{data[1]}.{data[2]}.{data[3]}";
        return true;
    }

    public static bool IsAllowed(int did) => Targets.Any(item => item.Did == did);
    public static string Name(int did) => Targets.FirstOrDefault(item => item.Did == did)?.Label ?? "Неподдерживаемый DID";

    public static byte[] BuildReadRequest(int did)
    {
        if (!IsAllowed(did)) throw new ArgumentOutOfRangeException(nameof(did), "Only explicitly catalogued read-only BUI DIDs are allowed.");
        // Clone the exact packet layout that already read DID 0220 on the bike.
        // Host SWA sequence = 0. CAN record occupies bytes 8..18,
        // CID 003A big-endian at 8..9; ISO-TP request at 11..14.
        var request = Protocol.ReadBui();
        request[13] = (byte)(did >> 8);
        request[14] = (byte)did;
        return request;
    }

    /// <summary>
    /// Strict structural candidate only, NOT a verified decoded parameter.
    /// Checks observed initial BUI-local reply, DLC, single-frame ISO-TP,
    /// positive UDS SID 62, exact requested DID. Ignores bytes after the CAN record.
    /// </summary>
    public static bool TryPositiveRead(byte[]? raw, int did, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (!IsAllowed(did) || raw == null || raw.Length != Protocol.Len) return false;
        if (raw[0] != 2 || raw[1] != 0 || raw[2] != 0 || raw[3] != 0 ||
            raw[6] != 2 || raw[7] != 1 || raw[8] != 0 || raw[9] != 0x3D)
            return false;
        int dlc = raw[10], singleFrameSize = raw[11];
        if (dlc < 5 || dlc > 8 || singleFrameSize != dlc - 1 ||
            singleFrameSize < 4 || singleFrameSize > 7)
            return false;
        if (raw[12] != 0x62 || raw[13] != (byte)(did >> 8) || raw[14] != (byte)did)
            return false;
        int size = singleFrameSize - 3; // excludes UDS SID and two DID bytes
        value = raw.AsSpan(15, size).ToArray();
        return true;
    }
}
