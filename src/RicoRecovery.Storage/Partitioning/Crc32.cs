namespace RicoRecovery.Storage.Partitioning;

public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (var index = 0; index < table.Length; index++)
        {
            var current = (uint)index;

            for (var bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? 0xEDB88320u ^ (current >> 1)
                    : current >> 1;
            }

            table[index] = current;
        }

        return table;
    }
}
