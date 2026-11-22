namespace RicoRecovery.Core.Models;

public sealed record UnknownSignatureCandidate(string PrefixHex, long Count, long FirstOffset);
