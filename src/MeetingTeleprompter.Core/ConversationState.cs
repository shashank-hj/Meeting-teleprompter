using System.Text.RegularExpressions;

namespace MeetingTeleprompter.Core;

public sealed class ConversationState
{
    private readonly int _maxRecent;
    private readonly Queue<StableUtterance> _recent = new();
    private readonly HashSet<string> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _topics = [];
    private readonly List<string> _decisions = [];
    private readonly List<string> _questions = [];

    public ConversationState(int maxRecent = 12) => _maxRecent = maxRecent;

    public void Add(StableUtterance utterance)
    {
        _recent.Enqueue(utterance);
        while (_recent.Count > _maxRecent) _recent.Dequeue();

        foreach (Match match in Regex.Matches(utterance.Text, @"\b(?:[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*|[A-Z]{2,8})\b"))
            _entities.Add(match.Value);

        if (utterance.Text.Contains('?', StringComparison.Ordinal))
            _questions.Add(utterance.Text.Trim());
        if (Regex.IsMatch(utterance.Text, @"\b(decide|agreed|we will|let's)\b", RegexOptions.IgnoreCase))
            _decisions.Add(utterance.Text.Trim());

        var words = Regex.Matches(utterance.Text.ToLowerInvariant(), @"\b[a-z]{4,}\b")
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .Distinct()
            .Take(5);
        _topics.Clear();
        _topics.AddRange(words);
        while (_entities.Count > 32) _entities.Remove(_entities.First());
        if (_decisions.Count > 5) _decisions.RemoveRange(0, _decisions.Count - 5);
        if (_questions.Count > 5) _questions.RemoveRange(0, _questions.Count - 5);
    }

    public ConversationSnapshot Snapshot()
    {
        var recent = _recent.ToArray();
        var summary = string.Join(" | ", new[]
        {
            _topics.Count > 0 ? $"Topics: {string.Join(", ", _topics)}" : null,
            _entities.Count > 0 ? $"Entities: {string.Join(", ", _entities.Take(8))}" : null,
            _decisions.Count > 0 ? $"Decisions: {string.Join("; ", _decisions.TakeLast(2))}" : null,
            _questions.Count > 0 ? $"Open questions: {string.Join("; ", _questions.TakeLast(2))}" : null
        }.Where(x => x is not null));
        return new(recent, summary[..Math.Min(summary.Length, 2400)], _topics.ToArray(), _entities.ToArray(), _decisions.TakeLast(5).ToArray(), _questions.TakeLast(5).ToArray());
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "what", "when", "where", "which", "have", "will", "about", "there", "their"
    };
}
