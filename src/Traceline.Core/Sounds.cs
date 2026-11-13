namespace Traceline.Core;

/// <summary>
/// Alert sounds: three that are made on the fly (so nothing has to be shipped) plus any .wav, .mp3 or .ogg
/// files placed in the "sounds" folder of the data folder, by hand or through the UI.
/// </summary>
public sealed class Sounds(string dataFolder)
{
    public static readonly string[] BuiltIn = ["Beep", "Chime", "Alarm"];
    private static readonly string[] Extensions = [".wav", ".mp3", ".ogg"];

    private string Folder => Path.Combine(dataFolder, "sounds");

    public IEnumerable<string> Names() =>
        BuiltIn.Concat(Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder).Select(Path.GetFileName).Where(n => n is not null && Extensions.Contains(Path.GetExtension(n).ToLowerInvariant())).Order()!
            : []);

    /// <summary>The sound's bytes and media type, or null if there is no such sound.</summary>
    public (byte[] Bytes, string MediaType)? Get(string name)
    {
        if (BuiltIn.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } builtIn) return (Tone(builtIn), "audio/wav");
        if (!IsSafe(name)) return null;
        var path = Path.Combine(Folder, name);
        if (!File.Exists(path)) return null;
        return (File.ReadAllBytes(path), Path.GetExtension(name).ToLowerInvariant() switch { ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", _ => "audio/wav" });
    }

    public void Save(string name, byte[] bytes)
    {
        if (!IsSafe(name)) throw new ArgumentException("Use a .wav, .mp3 or .ogg file with a plain name.");
        if (bytes.Length is 0 or > 5_000_000) throw new ArgumentException("The sound file must be smaller than 5 MB.");
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, name), bytes);
    }

    public void Delete(string name)
    {
        if (IsSafe(name)) File.Delete(Path.Combine(Folder, name));
    }

    private static bool IsSafe(string name) =>
        name.Length is > 4 and < 100 && name == Path.GetFileName(name) && !name.StartsWith('.')
        && Extensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>A short 16-bit mono WAV: a single beep, a rising two-note chime, or three urgent pulses.</summary>
    public static byte[] Tone(string name)
    {
        const int rate = 22050;
        (double Hz, double Seconds)[] notes = name switch
        {
            "Chime" => [(660, 0.18), (0, 0.03), (880, 0.32)],
            "Alarm" => [(988, 0.16), (0, 0.08), (988, 0.16), (0, 0.08), (988, 0.16)],
            _ => [(880, 0.35)],
        };
        var samples = new List<short>();
        foreach (var (hz, seconds) in notes)
        {
            var count = (int)(rate * seconds);
            for (var i = 0; i < count; i++)
            {
                // Fade in and out over a few milliseconds so the tone does not click.
                var envelope = Math.Min(1, Math.Min(i, count - i) / (rate * 0.008));
                samples.Add((short)(hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * 9000 * envelope));
            }
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }
}
