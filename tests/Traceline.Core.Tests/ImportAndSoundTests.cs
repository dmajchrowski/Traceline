using System.Text;
using Traceline.Core;

namespace Traceline.Core.Tests;

public class TextImportTests
{
    private const string Export = """
        Host Information
        1,,
        2,loop1.example.net,198.51.100.254
        3,198.51.100.29
        4,dest.example.com,203.0.113.154
        Sample Information
        "9/11/2014 11:13:29 PM",N/A,10,30,50
        "9/11/2014 11:13:30 PM",N/A,10,*,51
        "9/11/2014 11:13:31 PM",N/A,12,20,*
        """;

    [Fact]
    public void A_text_export_becomes_a_closed_session_with_the_same_numbers()
    {
        using var store = new Store(":memory:");
        var target = SessionArchive.ImportText(store, Export + Environment.NewLine + Environment.NewLine, "");   // real files end with a line break
        Assert.True(target.Closed);
        Assert.Equal("203.0.113.154", target.Address);
        Assert.StartsWith("dest.example.com", target.Name);

        var samples = store.Samples(target.Id, 0, long.MaxValue);
        Assert.Equal(3, samples.Count);
        Assert.Equal("|198.51.100.254|198.51.100.29|203.0.113.154", string.Join('|', store.Route(samples[0].RouteId)));
        Assert.True(float.IsNaN(samples[0].Rtts[0]));          // "N/A": that hop never answered
        Assert.Equal(new float[] { 10, 30, 50 }, samples[0].Rtts[1..]);
        Assert.True(float.IsNaN(samples[1].Rtts[2]));          // "*": lost
        Assert.True(float.IsNaN(samples[2].Rtts[3]));
        Assert.Equal(1000, samples[1].Ts - samples[0].Ts);

        var settings = new AppSettings { OwnerLookup = false, ReverseDns = false };
        var stats = new ViewBuilder(store, new HostDirectory(store, () => settings), _ => new TraceConfig()).Stats(target.Id, samples[0].Ts, samples[^1].Ts + 1);
        Assert.Equal(4, stats.Count);
        Assert.Equal((3, 1, 50.5), (stats[3].Count, stats[3].Lost, stats[3].Avg));
        Assert.Equal(3, store.Rollups(target.Id, 0, long.MaxValue).Sum(r => r.Aggs[0].N));
    }

    [Theory]
    [InlineData("just,some,numbers\n1,2,3")]
    [InlineData("Host Information\n1,a,192.0.2.1\nSample Information\nN/A,10")]   // no sample times
    public void Other_text_is_refused_with_an_explanation(string text)
    {
        using var store = new Store(":memory:");
        Assert.Throws<ArgumentException>(() => SessionArchive.ImportText(store, text, ""));
        Assert.Empty(store.Samples(1, 0, long.MaxValue));
    }
}

public class SoundTests
{
    [Theory]
    [InlineData("Beep")]
    [InlineData("Chime")]
    [InlineData("Alarm")]
    public void Built_in_sounds_are_valid_wav_files(string name)
    {
        var wav = Sounds.Tone(name);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.InRange(wav.Length, 5_000, 60_000);
    }

    [Fact]
    public void Uploaded_sounds_are_listed_served_and_removed_and_odd_names_are_refused()
    {
        var folder = Directory.CreateTempSubdirectory("traceline-sounds").FullName;
        try
        {
            var sounds = new Sounds(folder);
            Assert.Equal(Sounds.BuiltIn, sounds.Names());
            sounds.Save("door bell.wav", Sounds.Tone("Chime"));
            Assert.Contains("door bell.wav", sounds.Names());
            Assert.Equal("audio/wav", sounds.Get("door bell.wav")!.Value.MediaType);
            Assert.Equal("audio/wav", sounds.Get("chime")!.Value.MediaType);
            Assert.Null(sounds.Get("missing.wav"));
            Assert.Throws<ArgumentException>(() => sounds.Save("..\\evil.wav", [1]));
            Assert.Throws<ArgumentException>(() => sounds.Save("program.exe", [1]));
            Assert.Null(sounds.Get("..\\traceline.db"));
            sounds.Delete("door bell.wav");
            Assert.Equal(Sounds.BuiltIn, sounds.Names());
        }
        finally { Directory.Delete(folder, true); }
    }
}
