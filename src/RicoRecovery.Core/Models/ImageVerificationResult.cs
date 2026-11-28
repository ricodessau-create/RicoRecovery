namespace RicoRecovery.Core.Models;

public sealed record ImageVerificationResult(bool Identical, long FirstMismatch, long BytesCompared);
