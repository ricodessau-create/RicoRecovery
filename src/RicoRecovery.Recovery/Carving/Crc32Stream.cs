namespace RicoRecovery.Recovery.Carving;

internal sealed class Crc32Stream
{
    private static readonly uint[] Table = BuildTable();

    private uint state = 0xFFFFFFFF;

    public uint Value => ~state;

    public void Update(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            state = Table[(state ^ value) & 0xFF] ^ (state >> 8);
        }
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (var index = 0; index < table.Length; index++)
        {
            var current = (uint)index;

            for (var bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0 ? 0xEDB88320u ^ (current >> 1) : current >> 1;
            }

            table[index] = current;
        }

        return table;
    }
}
