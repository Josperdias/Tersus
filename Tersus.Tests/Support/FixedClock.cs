using Tersus.Core;

namespace Tersus.Tests.Support;

public sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>IProgress that runs the callback on the reporting thread (the framework one posts to the thread pool, which makes tests racy).</summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
