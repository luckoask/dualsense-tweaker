namespace DualSenseTweaker.Core.Device;

/// <summary>
/// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) used by DualSense Bluetooth reports.
/// The controller expects the CRC to cover a one-byte seed (the HIDP header: 0xA1 input,
/// 0xA2 output, 0xA3 feature) followed by the report bytes, like Linux' crc32_le(seed) + data.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFFu, data);

    public static uint Compute(byte seed, ReadOnlySpan<byte> data) =>
        ~Update(Update(0xFFFFFFFFu, [seed]), data);

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
