using System.Diagnostics;

namespace Tersus.App.Smoke;

/// <summary>Collects WPF data-binding warnings (a mistyped property path is otherwise silent) so the automated check can fail on them.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_gate)
        {
            if (_messages.Count < 500)
            {
                _messages.Add(message.Trim());
            }
        }
    }
}
