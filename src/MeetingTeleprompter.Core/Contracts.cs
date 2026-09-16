namespace MeetingTeleprompter.Core;

public static class Protocol
{
    public const int Version = 1;
}

public enum WorkPriority
{
    AudioCapture = 0,
    Asr = 1,
    UserRetrieval = 2,
    UserGeneration = 3,
    ProactiveRetrieval = 4,
    ProactiveGeneration = 5,
    Indexing = 6
}

public enum TranscriptKind { Partial, Final }

public sealed record AudioFrame(
    string Channel,
    long Sequence,
    DateTimeOffset CapturedAt,
    byte[] Pcm16KhzMono,
    bool IsFinal = true);

public sealed record TranscriptEvent(
    string SegmentId,
    string Channel,
    TranscriptKind Kind,
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double Confidence,
    DateTimeOffset CreatedAt);

public sealed record StableUtterance(
    string SegmentId,
    string Channel,
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double Confidence,
    DateTimeOffset CreatedAt);

public sealed record SourceReference(
    string Id,
    string Type,
    string CanonicalLocation,
    string Title,
    DateTimeOffset? FetchedAt,
    string ContentHash,
    int? Page,
    int ChunkIndex);

public sealed record EvidenceChunk(
    string Id,
    string Text,
    SourceReference Source,
    double Score);

public sealed record ConversationSnapshot(
    IReadOnlyList<StableUtterance> RecentUtterances,
    string Summary,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenQuestions);

public sealed record TriggerDecision(bool ShouldGenerate, string Reason, double Novelty);

public sealed record SuggestionRequest(
    string RequestId,
    WorkPriority Priority,
    string Route,
    ConversationSnapshot Conversation,
    IReadOnlyList<EvidenceChunk> Evidence,
    CancellationToken CancellationToken);

public sealed record Suggestion(
    string Id,
    string Text,
    string Route,
    bool IsGeneratedAssistance,
    IReadOnlyList<SourceReference> Sources,
    DateTimeOffset CreatedAt);

public interface ITranscriber
{
    IAsyncEnumerable<TranscriptEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> frames,
        CancellationToken cancellationToken);
}

public interface IEmbedder
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IRetriever
{
    Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

public interface IGenerator
{
    IAsyncEnumerable<string> StreamAsync(
        SuggestionRequest request,
        CancellationToken cancellationToken);
}

public interface IAudioSource
{
    IAsyncEnumerable<AudioFrame> CaptureAsync(
        CancellationToken cancellationToken);
}
