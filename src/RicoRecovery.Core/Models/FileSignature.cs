namespace RicoRecovery.Core.Models;

public sealed record FileSignature(
    string Name,
    string Extension,
    FileCategory Category,
    int HeaderOffset,
    byte[] Header,
    ExtentStrategy Strategy,
    long MinSize,
    long MaxSize);
