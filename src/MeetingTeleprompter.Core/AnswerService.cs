using System.Text;
using System.Text.RegularExpressions;

namespace MeetingTeleprompter.Core;

// One policy for live suggestions and typed questions. General answers never inherit citations.
public sealed class AnswerService(IRetriever retriever, IGenerator generator)
{
    public static bool RequiresSource(string question, string route) => route is "evidence" or "check" ||
        Regex.IsMatch(question, @"\b(our|my|ours|company|client|customer|internal|project|deadline|budget|salary|latest|current|today|tomorrow|yesterday|202[6-9]|203\d|diagnos\w*|dosage|medication|legal|lawsuit|invest\w*|stock price)\b", RegexOptions.IgnoreCase);

    public async Task<Suggestion> AnswerAsync(ConversationSnapshot conversation, string route, bool allowGeneral,
        WorkPriority priority, CancellationToken ct, Action<string>? status = null, Action<AnswerBasis, string>? draft = null)
    {
        var question = conversation.RecentUtterances.LastOrDefault()?.Text ?? "";
        if (string.IsNullOrWhiteSpace(question)) throw new InvalidOperationException("Ask a question or wait for speech first.");
        status?.Invoke("Searching your sources…");
        // Bound prompt size for the installed 2048-token local model.
        var evidence = (await retriever.SearchAsync(question, 3, ct))
            .Select(x => x with { Text = x.Text[..Math.Min(x.Text.Length, 650)] }).ToArray();
        var basis = evidence.Length > 0 ? AnswerBasis.Sources : AnswerBasis.GeneralKnowledge;
        if (basis == AnswerBasis.Sources && route == "evidence") return Result(Passages(evidence), false, basis, evidence);
        if (basis == AnswerBasis.Sources)
        {
            status?.Invoke("Drafting from your sources…");
            string text;
            try { text = await Generate(basis, evidence); }
            catch (InferenceResourceException ex) { return Result(ex.Message + "\n\n" + Passages(evidence), false, basis, evidence); }
            if (!text.Contains("INSUFFICIENT_EVIDENCE", StringComparison.OrdinalIgnoreCase))
            {
                var citations = Regex.Matches(text, @"\[(\d+)\]");
                if (text.Length > 0 && citations.Count > 0 && citations.All(m => int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= evidence.Length))
                    return Result(text, true, basis, evidence);
                return Result("I couldn't verify the draft's citations. Check these source passages instead:\n\n" + Passages(evidence), false, basis, evidence);
            }
        }
        if (!allowGeneral || RequiresSource(question, route))
            return Result("I don't have enough source evidence to answer this reliably. Add a relevant document or URL, or ask for general background. I won't guess private details, current facts, or verify a claim without evidence.", false, AnswerBasis.NeedsSource, []);

        status?.Invoke("Drafting general knowledge · not verified by your sources…");
        string general;
        try { general = await Generate(AnswerBasis.GeneralKnowledge, []); }
        catch (InferenceResourceException ex) { return Result(ex.Message, false, AnswerBasis.ResourceLimited, []); }
        // A model cannot turn an unsourced answer into an apparently sourced one.
        general = Regex.Replace(general, @"\[\d+\]", "").Trim();
        if (string.IsNullOrWhiteSpace(general) || general.Contains("INSUFFICIENT_EVIDENCE", StringComparison.OrdinalIgnoreCase))
            return Result("I can't answer this confidently from local knowledge. Please add a reliable source or clarify the question.", false, AnswerBasis.NeedsSource, []);
        return Result(general, true, AnswerBasis.GeneralKnowledge, []);

        async Task<string> Generate(AnswerBasis mode, IReadOnlyList<EvidenceChunk> passages)
        {
            var output = new StringBuilder();
            var request = new SuggestionRequest(Guid.NewGuid().ToString("N"), priority, route, conversation, passages, ct, mode);
            draft?.Invoke(mode, "");
            var lastUpdate = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var token in generator.StreamAsync(request, ct))
            {
                output.Append(token);
                if (lastUpdate.ElapsedMilliseconds < 80) continue;
                draft?.Invoke(mode, output.ToString());
                lastUpdate.Restart();
            }
            draft?.Invoke(mode, output.ToString());
            return output.ToString().Trim();
        }
        Suggestion Result(string text, bool generated, AnswerBasis mode, IReadOnlyList<EvidenceChunk> passages) =>
            new(Guid.NewGuid().ToString("N"), text, route, generated, passages.Select(x => x.Source).ToArray(), DateTimeOffset.UtcNow, mode, question, passages);
    }

    private static string Passages(IReadOnlyList<EvidenceChunk> chunks) => string.Join("\n\n", chunks.Select((x, i) => $"[{i + 1}] {x.Text}"));
}

public static class SearchTerms
{
    private static readonly HashSet<string> Stop = new("a an the is are was were be been being do does did can could should would will shall have has had how what why when where who which that this these those there their they them it its to of for from in on at by with as and or but not we you i me us please tell about explain give answer know need needs".Split(' '));
    public static string[] Meaningful(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).Where(x => x.Length > 1 && !Stop.Contains(x)).Distinct().Take(24).ToArray();
    public static bool Overlaps(string query, string text)
    {
        var terms = Meaningful(query);
        var target = MeaningfulUnbounded(text);
        return terms.Count(target.Contains) >= Math.Min(3, Math.Max(1, (int)Math.Ceiling(terms.Length * .5))) && terms.Length > 0;
    }
    private static HashSet<string> MeaningfulUnbounded(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).ToHashSet();
}
