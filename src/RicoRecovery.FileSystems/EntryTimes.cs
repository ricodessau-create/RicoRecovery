namespace RicoRecovery.FileSystems;

internal static class EntryTimes
{
    public static DateTimeOffset? FromDos(ushort date, ushort time)
    {
        var day = date & 0x1F;
        var month = (date >> 5) & 0x0F;
        var year = (date >> 9) + 1980;
        var second = (time & 0x1F) * 2;
        var minute = (time >> 5) & 0x3F;
        var hour = time >> 11;

        return Create(year, month, day, hour, minute, second, TimeSpan.Zero);
    }

    public static DateTimeOffset? FromExFat(uint value, byte utcOffset)
    {
        var second = (int)(value & 0x1F) * 2;
        var minute = (int)((value >> 5) & 0x3F);
        var hour = (int)((value >> 11) & 0x1F);
        var day = (int)((value >> 16) & 0x1F);
        var month = (int)((value >> 21) & 0x0F);
        var year = (int)(value >> 25) + 1980;
        var offset = TimeSpan.Zero;

        if ((utcOffset & 0x80) != 0)
        {
            var quarters = utcOffset & 0x7F;

            if (quarters >= 64)
            {
                quarters -= 128;
            }

            var candidate = TimeSpan.FromMinutes(quarters * 15);

            if (Math.Abs(candidate.TotalHours) <= 14)
            {
                offset = candidate;
            }
        }

        return Create(year, month, day, hour, minute, second, offset);
    }

    public static DateTimeOffset? FromFileTime(long ticks)
    {
        if (ticks <= 0 || ticks > 2650467743999999999L)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero);
    }

    public static DateTimeOffset? FromUnixSeconds(long seconds)
    {
        if (seconds <= 0 || seconds > 253402300799L)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static DateTimeOffset? Create(int year, int month, int day, int hour, int minute, int second, TimeSpan offset)
    {
        if (year < 1980 || year > 2107 || month < 1 || month > 12)
        {
            return null;
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, offset);
    }
}
