namespace Tersus.Core;

/// <summary>Time source. Every age rule goes through this so tests can pin "now".</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    private SystemClock()
    {
    }

    public DateTime UtcNow => DateTime.UtcNow;
}
