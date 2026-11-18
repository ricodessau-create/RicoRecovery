namespace RicoRecovery.Core.Models;

public sealed record ScanLogEntry(DateTimeOffset Timestamp, string Level, string Message);
