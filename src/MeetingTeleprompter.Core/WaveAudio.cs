using System.Text;

namespace MeetingTeleprompter.Core;

public static class WaveAudio
{
    public static byte[] ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length > 20 * 1024 * 1024) throw new InvalidOperationException("WAV exceeds 20 MB.");
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidOperationException("Expected WAV.");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidOperationException("Expected WAV.");
        var valid = false;
        var floating = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadInt32();
            var end = reader.BaseStream.Position + length;
            if (length < 0 || end > reader.BaseStream.Length) throw new InvalidOperationException("Invalid WAV chunk.");
            if (chunk == "fmt ")
            {
                if (length < 16) throw new InvalidOperationException("Invalid WAV format header.");
                var format = reader.ReadInt16(); var channels = reader.ReadInt16(); var rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt16(); var bits = reader.ReadInt16();
                floating = format == 3 && bits == 32;
                valid = channels == 1 && rate == 16000 && ((format == 1 && bits == 16) || floating);
            }
            if (chunk == "data")
            {
                if (!valid) throw new InvalidOperationException("Choose mono PCM16 or float32 WAV at 16 kHz.");
                if (length % (floating ? 4 : 2) != 0) throw new InvalidOperationException("WAV contains an incomplete sample.");
                var audio = reader.ReadBytes(length);
                if (!floating) return audio;
                var pcm = new byte[audio.Length / 2];
                for (var i = 0; i < audio.Length / 4; i++)
                {
                    var sample = BitConverter.ToSingle(audio, i * 4);
                    if (!float.IsFinite(sample)) throw new InvalidOperationException("WAV contains a non-finite sample.");
                    var value = (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
                    pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
                }
                return pcm;
            }
            reader.BaseStream.Position = end + (length % 2);
        }
        throw new InvalidOperationException("No WAV audio data.");
    }
}
