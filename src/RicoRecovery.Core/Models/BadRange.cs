namespace RicoRecovery.Core.Models;

public sealed record BadRange(long Offset, long Length, int Attempts);
