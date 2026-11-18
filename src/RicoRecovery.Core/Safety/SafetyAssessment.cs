namespace RicoRecovery.Core.Safety;

public sealed record SafetyAssessment(bool IsSafe, string Message)
{
    public static SafetyAssessment Safe { get; } = new(true, string.Empty);

    public static SafetyAssessment Unsafe(string message) => new(false, message);
}
