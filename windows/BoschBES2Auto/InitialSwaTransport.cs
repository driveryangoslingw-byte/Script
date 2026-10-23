namespace BoschBES2Auto;

/// <summary>
/// Only the initial Sliding Window A BUI exchange verified on this bicycle.
/// This is not a sequence/ACK engine, general CAN router, or ISO-TP reassembler.
/// </summary>
public static class InitialSwaTransport
{
    public const int CanRecordOffset = 8;
    public const int CanRecordSize = 11;

    public static byte[] BuildBuiRead(int did)
    {
        if (!BuiResearch.IsAllowed(did))
            throw new ArgumentOutOfRangeException(nameof(did), "Only catalogued read-only BUI DIDs are allowed.");
        var raw = new byte[Protocol.Len];
        raw[0] = 2; // initial host sequence and ACK remain zero
        raw[4] = 0xFF; raw[5] = 0xFF;
        raw[6] = CanRecordSize;
        raw[9] = 0x3A; raw[10] = 4; raw[11] = 3;
        raw[12] = 0x22; raw[13] = (byte)(did >> 8); raw[14] = (byte)did;
        return raw;
    }

    public static bool TryReadBuiSingleFrame(byte[]? raw, out byte[] uds)
    {
        uds = Array.Empty<byte>();
        if (raw is null || raw.Length != Protocol.Len || raw[0] != 2 || raw[1] != 0 ||
            raw[2] != 0 || raw[3] != 0 || raw[6] != 2 || raw[7] != 1 ||
            raw[8] != 0 || raw[9] != 0x3D) return false;
        // b4..5 advertise queue capacity. The special local-reply b6 encodes
        // device TX sequence + 2, not the generic record byte count.
        int dlc = raw[10], size = raw[11];
        if (dlc is < 2 or > 8 || size != dlc - 1 || size is < 1 or > 7) return false;
        uds = raw.AsSpan(12, size).ToArray();
        // Ignore the remainder of the fixed CAN record and all subsequent RAM.
        return true;
    }

    public static bool TryReadPositiveBui(byte[]? raw, int did, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (!BuiResearch.IsAllowed(did) || !TryReadBuiSingleFrame(raw, out var uds) ||
            uds.Length < 4 || uds[0] != 0x62 || uds[1] != (byte)(did >> 8) || uds[2] != (byte)did)
            return false;
        value = uds.AsSpan(3).ToArray();
        return true;
    }
}
