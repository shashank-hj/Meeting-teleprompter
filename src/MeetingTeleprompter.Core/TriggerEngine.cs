namespace MeetingTeleprompter.Core;

public sealed class TriggerEngine(TimeSpan cooldown)
{
    private DateTimeOffset _lastTriggered = DateTimeOffset.MinValue;
    private string _lastText = string.Empty;

    public TriggerDecision Evaluate(StableUtterance utterance, ConversationSnapshot snapshot)
    {
        var text = utterance.Text.Trim();
        var question = text.EndsWith('?') || System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^\s*(?:(?:so|and|okay|well|but)[, ]+)?(?:what|why|how|when|where|who|which|could|should|can|do|does|did|is|are|will|would)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var claim = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(always|never|must|guaranteed|\d+(?:\.\d+)?%?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var novelty = WordNovelty(_lastText, text);
        var topicChange = novelty >= 0.8;
        // Distinct meeting questions must not wait behind a previous suggestion.
        var repeatedQuestion = question && string.Equals(NormalizeQuestion(_lastText), NormalizeQuestion(text), StringComparison.OrdinalIgnoreCase);
        var allowed = question ? !repeatedQuestion : DateTimeOffset.UtcNow - _lastTriggered >= cooldown;
        var decisionOrRisk = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(decide|agreed|risk|blocked|deadline|concern|action item)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var reason = question ? "question" : claim ? "claim" : decisionOrRisk ? "decision_or_risk" : topicChange ? "topic_change" : string.Empty;
        var should = allowed && (question || novelty > .15) && reason.Length > 0 && utterance.Confidence >= 0.4;
        if (should)
        {
            _lastTriggered = DateTimeOffset.UtcNow;
            _lastText = text;
        }
        return new(should, reason, novelty);
    }

    public void Reset() => (_lastTriggered, _lastText) = (DateTimeOffset.MinValue, string.Empty);

    public static bool IsTopicShift(string previous, string current)
    {
        var a = SearchTerms.Meaningful(previous).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = SearchTerms.Meaningful(current).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a.Count >= 2 && b.Count >= 3 && (double)a.Intersect(b).Count() / Math.Min(a.Count, b.Count) < .2;
    }

    private static string NormalizeQuestion(string text) => text.Trim().TrimEnd('.', '?', '!').Trim();

    private static double WordNovelty(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous)) return 1;
        var a = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return 1 - (a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count());
    }
}
