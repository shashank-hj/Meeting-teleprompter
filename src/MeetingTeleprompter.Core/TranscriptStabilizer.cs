namespace MeetingTeleprompter.Core;

public sealed class TranscriptStabilizer
{
    private readonly Dictionary<string, TranscriptEvent> _last = new();
    private readonly HashSet<string> _finals = new();
    private readonly Queue<string> _order = new();
    public IReadOnlyList<StableUtterance> Accept(TranscriptEvent entry)
    {
        if (entry.Kind != TranscriptKind.Final || string.IsNullOrWhiteSpace(entry.Text)) return [];
        var key = entry.Channel + ":" + entry.SegmentId;
        if (!_finals.Add(key)) return [];
        _order.Enqueue(key);
        while (_order.Count > 2048) _finals.Remove(_order.Dequeue());
        var words = entry.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_last.TryGetValue(entry.Channel, out var previous) && entry.Start < previous.End)
        {
            var prior = previous.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var count = Math.Min(words.Length, prior.Length); count >= 2; count--)
                if (prior.TakeLast(count).SequenceEqual(words.Take(count), StringComparer.OrdinalIgnoreCase))
                { words = words.Skip(count).ToArray(); break; }
        }
        _last[entry.Channel] = entry;
        if (words.Length == 0) return [];
        return [new(entry.SegmentId, entry.Channel, string.Join(' ', words), entry.Start, entry.End, entry.Confidence, entry.CreatedAt)];
    }
}
