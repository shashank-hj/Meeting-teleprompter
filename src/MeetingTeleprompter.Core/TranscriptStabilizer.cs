namespace MeetingTeleprompter.Core;

public sealed class TranscriptStabilizer
{
    private readonly Dictionary<string, TranscriptEvent> _last = new();
    private readonly Dictionary<string, (TranscriptEvent Entry, TranscriptEvent? Previous)> _finals = new();
    private readonly Queue<string> _order = new();
    public IReadOnlyList<StableUtterance> Accept(TranscriptEvent entry)
    {
        if (entry.Kind != TranscriptKind.Final || string.IsNullOrWhiteSpace(entry.Text)) return [];
        var key = entry.Channel + ":" + entry.SegmentId;
        var correction = _finals.TryGetValue(key, out var existing);
        if (correction && existing.Entry.Text == entry.Text) return [];
        var previous = correction ? existing.Previous : _last.GetValueOrDefault(entry.Channel);
        _finals[key] = (entry, previous);
        if (!correction) _order.Enqueue(key);
        while (_order.Count > 2048) _finals.Remove(_order.Dequeue());
        var words = entry.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (previous is not null && entry.Start < previous.End)
        {
            var prior = previous.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var count = Math.Min(words.Length, prior.Length); count >= 2; count--)
                if (prior.TakeLast(count).Select(Normalize).SequenceEqual(words.Take(count).Select(Normalize), StringComparer.OrdinalIgnoreCase))
                { words = words.Skip(count).ToArray(); break; }
        }
        if (!correction || _last.GetValueOrDefault(entry.Channel)?.SegmentId == entry.SegmentId) _last[entry.Channel] = entry;
        if (words.Length == 0 && !correction) return [];
        return [new(entry.SegmentId, entry.Channel, string.Join(' ', words), entry.Start, entry.End, entry.Confidence, entry.CreatedAt, correction)];
    }
    private static string Normalize(string word) => word.Trim('.', ',', '!', '?', ':', ';', '"', '\'');
}
