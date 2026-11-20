namespace MeetingTeleprompter.Core;

// Deterministic 16 kHz segmenter, independent of capture hardware and VAD runtime.
public sealed class SpeechSegmenter(string channel, double threshold = .55, int endSilenceMs = 700)
{
    private const int Rate = 16000, PreRoll = 5120, MinimumSpeech = 1536, MaxSamples = 120000, Overlap = 7680;
    private readonly List<float> _audio = [];
    private readonly Queue<float> _preRoll = new();
    private long _samples, _start, _sequence;
    private int _speech, _silence;
    private bool _active, _partial;
    private DateTimeOffset? _origin;
    public bool Speaking { get; private set; }

    public IReadOnlyList<AudioFrame> Push(float[] samples, int count, double probability, DateTimeOffset endTime)
    {
        if (count <= 0) return [];
        _origin ??= endTime - TimeSpan.FromSeconds(count / (double)Rate);
        var frames = new List<AudioFrame>();
        var onset = Math.Clamp(threshold, .35, .8);
        Speaking = probability >= (_active ? onset - .18 : onset);
        if (!_active && Speaking)
        {
            _start = _samples - _preRoll.Count;
            _audio.AddRange(_preRoll); _preRoll.Clear(); _active = true;
        }
        _samples += count;
        if (!_active)
        {
            for (var i = 0; i < count; i++) _preRoll.Enqueue(samples[i]);
            while (_preRoll.Count > PreRoll) _preRoll.Dequeue();
            return frames;
        }
        for (var i = 0; i < count; i++) _audio.Add(samples[i]);
        if (Speaking) { _speech += count; _silence = 0; }
        else _silence += count;
        var enoughSpeech = _speech >= MinimumSpeech;
        var endpoint = _silence >= Math.Clamp(endSilenceMs, 400, 1400) * 16;
        // Prefer a brief natural pause after 3.5 s, but retain up to 7.5 s for long clauses.
        var softBoundary = _audio.Count >= 56000 && _silence >= 2560;
        var forcedBoundary = _audio.Count >= MaxSamples;
        if (endpoint || softBoundary || forcedBoundary)
        {
            if (enoughSpeech) frames.Add(Frame(true));
            _sequence++; _partial = false; _speech = 0;
            if (forcedBoundary && Speaking && enoughSpeech)
            {
                _audio.RemoveRange(0, _audio.Count - Overlap);
                _start = _samples - Overlap;
                // Overlap isn't new speech: don't emit an overlap-only duplicate at the next pause.
            }
            else
            {
                foreach (var sample in _audio.TakeLast(PreRoll)) _preRoll.Enqueue(sample);
                while (_preRoll.Count > PreRoll) _preRoll.Dequeue();
                _audio.Clear(); _active = false;
            }
            _silence = 0;
        }
        else if (!_partial && enoughSpeech && _audio.Count >= 40000 && _silence == 0)
        {
            frames.Add(Frame(false)); _partial = true;
        }
        return frames;
    }

    private AudioFrame Frame(bool final)
    {
        var bytes = new byte[_audio.Count * 2];
        for (var i = 0; i < _audio.Count; i++)
        {
            var value = (short)(Math.Clamp(_audio[i], -1f, 1f) * short.MaxValue);
            bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8);
        }
        return new(channel, _sequence, _origin!.Value + TimeSpan.FromSeconds(_start / (double)Rate), bytes, final);
    }
}

// Bounded capture queue: final speech replaces its queued preview, never the other way round.
public sealed class SpeechFrameQueue(int capacity = 12)
{
    private readonly LinkedList<AudioFrame> _frames = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _gate = new();
    private bool _completed;
    private Exception? _error;
    public bool Write(AudioFrame frame)
    {
        lock (_gate)
        {
            if (_completed) return false;
            for (var node = _frames.First; node is not null; node = node.Next)
                if (node.Value.Channel == frame.Channel && node.Value.Sequence == frame.Sequence)
                { if (!node.Value.IsFinal) node.Value = frame; return true; }
            if (_frames.Count >= capacity)
            {
                if (!frame.IsFinal) return false;
                var preview = _frames.First;
                while (preview is not null && preview.Value.IsFinal) preview = preview.Next;
                if (preview is null) return false;
                _frames.Remove(preview); _frames.AddLast(frame); return true;
            }
            _frames.AddLast(frame); _available.Release(); return true;
        }
    }
    public void Complete(Exception? error = null)
    {
        lock (_gate) { if (_completed) return; _completed = true; _error = error; _available.Release(); }
    }
    public async IAsyncEnumerable<AudioFrame> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            await _available.WaitAsync(ct);
            AudioFrame? frame;
            lock (_gate)
            {
                if (_frames.First is { } first) { frame = first.Value; _frames.RemoveFirst(); }
                else { if (_error is not null) throw new IOException("Audio capture failed.", _error); yield break; }
            }
            yield return frame;
        }
    }
}
