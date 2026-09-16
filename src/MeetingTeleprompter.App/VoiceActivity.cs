using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MeetingTeleprompter.App;

public interface IVoiceActivity : IDisposable
{
    bool IsSpeech(float[] samples, int count);
}

public sealed class EnergyVoiceActivity : IVoiceActivity
{
    public bool IsSpeech(float[] samples, int count)
    {
        double energy = 0;
        for (var i = 0; i < count; i++) energy += samples[i] * samples[i];
        return Math.Sqrt(energy / Math.Max(1, count)) > .008;
    }
    public void Dispose() { }
}

// Silero v5/v6 ONNX streaming contract: 512 samples + 64 context at 16 kHz.
public sealed class SileroVoiceActivity : IVoiceActivity
{
    private readonly InferenceSession _session;
    private readonly Queue<float> _pending = new();
    private float[] _state = new float[256];
    private readonly float[] _context = new float[64];
    private bool _last;
    public SileroVoiceActivity(string path)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new(path, options);
    }
    public bool IsSpeech(float[] samples, int count)
    {
        for (var i = 0; i < count; i++) _pending.Enqueue(samples[i]);
        var speech = false;
        var evaluated = false;
        while (_pending.Count >= 512)
        {
            var input = new float[576];
            Array.Copy(_context, input, 64);
            for (var i = 64; i < input.Length; i++) input[i] = _pending.Dequeue();
            using var result = _session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, new[] { 1, 576 })),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>()))
            });
            _last = result.First(x => x.Name == "output").AsTensor<float>().First() >= .5f;
            _state = result.First(x => x.Name == "stateN").AsTensor<float>().ToArray();
            Array.Copy(input, 512, _context, 0, 64);
            speech |= _last; evaluated = true;
        }
        return evaluated ? speech : _last;
    }
    public void Dispose() => _session.Dispose();
}
