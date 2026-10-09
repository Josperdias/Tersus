using Tersus.Core;

namespace Tersus.Tests.Support;

public sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
