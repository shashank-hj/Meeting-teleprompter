using System.Runtime.CompilerServices;

namespace MeetingTeleprompter.Core;

public sealed class InferenceResourceException(string message) : Exception(message);

public static class RuntimeMemoryBudget
{
    public const long ReserveBytes = 2L * 1024 * 1024 * 1024;
    public static bool CanLoad(string modelPath, bool alreadyLoaded, long? availableBytes = null)
    {
        var available = availableBytes ?? RuntimeResourceMonitor.AvailableMemoryBytes();
        if (available is null) return true;
        var model = File.Exists(modelPath) ? new FileInfo(modelPath).Length : 0;
        var additional = alreadyLoaded ? 0 : (long)(model * 1.25) + 256L * 1024 * 1024;
        return available >= ReserveBytes + additional;
    }
}

// Generation is loaded only when useful and when it leaves the meeting and ASR headroom.
public sealed class BudgetedGenerator(IGenerator inner, NativeRuntimeManager runtimes, RuntimeSettings settings,
    Func<long?>? availableMemory = null) : IGenerator
{
    public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (settings.ConserveMemory && !RuntimeMemoryBudget.CanLoad(settings.GeneratorModel, runtimes.IsRunning("generator"), availableMemory?.Invoke()))
            throw new InferenceResourceException("There is not enough free RAM for generation while retaining 2 GiB of meeting headroom. Source evidence remains available; close unused applications or select a smaller model.");
        await runtimes.EnsureGenerationAsync(settings, ct);
        await foreach (var token in inner.StreamAsync(request, ct)) yield return token;
    }
}
