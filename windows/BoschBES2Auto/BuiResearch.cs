namespace BoschBES2Auto;

/// <summary>
/// A bounded list of publicly described BUI ReadDataByIdentifier candidates.
/// USB replies and semantic decoding have separate evidence. Language decoding
/// is supported by original firmware; the date's calendar-year origin is unknown.
/// Each run starts from a new handshake and sends exactly one read request.
/// </summary>
public static class BuiResearch
{
    public sealed record DidTarget(ushort Did, string Label, bool HardwareConfirmed)
    {
        public bool UsbReplyObserved { get; init; } = HardwareConfirmed;
        public string EvidenceStatus { get; init; } = HardwareConfirmed ? "VERIFIED" : "NOT_TESTED";
        public string Note { get; init; } = "";
        public override string ToString() => $"{Did:X4} · {Label} [{EvidenceStatus}]";
    }

    public static readonly IReadOnlyList<DidTarget> Targets = new[] {
        new DidTarget(0x0220, "Версия ПО Intuvia", true),
        new DidTarget(0x0221, "Версия аппаратного обеспечения Intuvia", true),
        new DidTarget(0x0222, "Дата производства: упакованные поля", false) {
            UsbReplyObserved = true, EvidenceStatus = "RAW_ONLY",
            Note = "День, месяц и код года выделены по прошивке. Базовый год ещё не установлен." },
        new DidTarget(0x0230, "Аппаратный вариант: сырой код", false) {
            UsbReplyObserved = true, EvidenceStatus = "RAW_ONLY", Note = "Ответ получен; таблица вариантов не установлена." },
        new DidTarget(0x0245, "Максимум скорости поездки: сырой счётчик", false) {
            UsbReplyObserved = true, EvidenceStatus = "RAW_ONLY", Note = "Рекорд поездки, не ограничитель ассиста. Масштаб не установлен." },
        new DidTarget(0x0249, "Общий пробег Intuvia (метры → км)", true),
        new DidTarget(0x024A, "Язык интерфейса", true) {
            Note = "Таблица кодов проверена по строкам и обработчикам прошивки. Код 7 остаётся неизвестным." },
        new DidTarget(0x024B, "Единицы расстояния: сырой код", false) {
            UsbReplyObserved = true, EvidenceStatus = "RAW_ONLY", Note = "Код получен. Связь внутренних enum с km/mi отдельно не доказана." },
        new DidTarget(0x024C, "Яркость экрана днём", false) {
            Note = "В исследованной прошивке обработчик чтения возвращает «не поддерживается»." },
        new DidTarget(0x0250, "Яркость экрана ночью", false) {
            Note = "Прошлый тест остановился до USB-open: NO_DEVICE. В дампе — неподдерживаемая функция." },
        new DidTarget(0x0251, "Контрастность дисплея", false) {
            Note = "Прошлый тест остановился до USB-open: NO_DEVICE. В дампе — неподдерживаемая функция." },
    };

    // Column indices of the original 11-language resource table. The same
    // getter is used by DID 024A and by the UI text selector in firmware 5.9.2.0.
    // Slot 7 has no language label; do not apply the UI's fallback as a decoder.
    private static readonly IReadOnlyDictionary<byte, string> LanguageNames = new Dictionary<byte, string> {
        [0] = "Deutsch", [1] = "English", [2] = "Français", [3] = "Italiano",
        [4] = "Español", [5] = "日本語", [6] = "Nederlands",
        [8] = "Dansk", [9] = "Svenska", [10] = "Português"
    };

    public static bool TryReadLanguage(byte[]? raw, out string language)
    {
        language = "";
        if (!TryPositiveRead(raw, 0x024A, out var data) || data.Length != 1 ||
            !LanguageNames.TryGetValue(data[0], out var name)) return false;
        language = name;
        return true;
    }

    /// <summary>
    /// Exact bit fields from original DID 0222 read/write handlers: day[15:11],
    /// month[10:7], year-code[6:0], transmitted big endian. This does not assign
    /// 1900/1980/2000 as the year origin and must not produce a calendar date.
    /// </summary>
    public static bool TryReadManufactureDateParts(byte[]? raw, out int day, out int month, out int yearCode)
    {
        day = month = yearCode = 0;
        if (!TryPositiveRead(raw, 0x0222, out var data) || data.Length != 2) return false;
        int packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data);
        int candidateDay = packed >> 11, candidateMonth = (packed >> 7) & 15, candidateYear = packed & 127;
        if (candidateDay is < 1 or > 31 || candidateMonth is < 1 or > 12 || candidateYear == 0) return false;
        day = candidateDay; month = candidateMonth; yearCode = candidateYear;
        return true;
    }

    public static string RawInterpretation(byte[] raw, int did)
    {
        if (did == 0x0222 && TryReadManufactureDateParts(raw, out int day, out int month, out int yearCode))
            return $"День: {day}; месяц: {month}; код года: {yearCode}. Полный календарный год не установлен.";
        if (did == 0x0245) return "Сохранённый максимум скорости поездки. Масштаб и единицы счётчика не установлены.";
        if (did == 0x0230) return "Код аппаратного варианта. Таблица вариантов не установлена.";
        if (did == 0x024B) return "Сырой код единиц. Название km/mi отдельно не подтверждено.";
        return "Расшифровка значения не подтверждена.";
    }

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
        return InitialSwaTransport.BuildBuiRead(did);
    }

    /// <summary>
    /// Strict structural candidate only, NOT a verified decoded parameter.
    /// Checks observed initial BUI-local reply, DLC, single-frame ISO-TP,
    /// positive UDS SID 62, exact requested DID. Ignores bytes after the CAN record.
    /// </summary>
    public static bool TryPositiveRead(byte[]? raw, int did, out byte[] value)
    {
        return InitialSwaTransport.TryReadPositiveBui(raw, did, out value);
    }
}

