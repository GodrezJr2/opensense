using OpenSense.Core.Control;

namespace OpenSense.Core.Engine;

/// <summary>The most recent telemetry samples, kept so a client (an AI agent, say) can look back over a run.</summary>
public sealed class TelemetryHistory(int capacity = 3600)
{
    private readonly Queue<Telemetry> _samples = new();
    private readonly object _gate = new();

    public void Add(Telemetry sample)
    {
        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > capacity)
                _samples.Dequeue();
        }
    }

    /// <summary>The samples of the last <paramref name="window"/> before the newest one, oldest first.</summary>
    public IReadOnlyList<Telemetry> Last(TimeSpan window)
    {
        lock (_gate)
        {
            if (_samples.Count == 0)
                return [];
            var since = _samples.Last().Timestamp - window;
            return [.. _samples.Where(s => s.Timestamp >= since)];
        }
    }
}
