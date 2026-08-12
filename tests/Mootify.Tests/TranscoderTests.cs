using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Transcoding;

namespace Mootify.Tests;

/// <summary>
/// The conversion itself needs FFmpeg and a real audio file, so these cover the parts that
/// don't: which files are candidates, and the argument list.
///
/// The argument list matters more than it looks. Every conversion in the library failed
/// because output went to "song.mp3.partial" — a temp name so a half-written file can't be
/// scanned — and FFmpeg picks its muxer from the extension. ".partial" means nothing to it,
/// so it died on "Unable to choose an output format" before reading a byte of audio, 698
/// times in a row, with the reason only in the server log.
/// </summary>
public sealed class TranscoderTests
{
    private static Transcoder Create(TranscodeOptions? options = null) =>
        new(new StaticOptionsMonitor<TranscodeOptions>(options ?? new TranscodeOptions()),
            NullLogger<Transcoder>.Instance);

    [Theory]
    // FLAC is indexed natively now — converting it would throw away the quality the
    // download was chosen for. Browsers that can't decode it get a cached copy instead.
    [InlineData("song.flac", false)]
    [InlineData("song.FLAC", false)]
    [InlineData("song.ogg", true)]
    [InlineData("song.m4a", true)]
    [InlineData("song.aac", true)]
    [InlineData("song.wma", true)]
    [InlineData("song.opus", true)]
    [InlineData("song.wav", true)]
    [InlineData("song.ape", true)]
    [InlineData("song.mp3", false)]
    [InlineData("cover.jpg", false)]
    [InlineData("notes.txt", false)]
    public void The_right_files_are_picked_for_conversion(string fileName, bool expected)
    {
        Assert.Equal(expected, Transcoder.NeedsTranscode(fileName));
    }

    [Fact]
    public void The_output_format_is_stated_explicitly()
    {
        // Regression. Without -f mp3 the ".partial" temp name leaves FFmpeg with no muxer to
        // choose and every file fails instantly.
        var args = Transcoder.BuildArguments(@"C:\music\song.flac", @"C:\music\song.mp3.partial", "320k");

        Assert.Contains("-f mp3", args);
    }

    [Fact]
    public void The_temp_output_still_carries_the_partial_suffix()
    {
        // The other half of the pairing: a half-written .mp3 must not be scannable.
        var args = Transcoder.BuildArguments(@"C:\music\song.flac", @"C:\music\song.mp3.partial", "320k");

        Assert.Contains("song.mp3.partial", args);
        Assert.DoesNotContain("\"C:\\music\\song.mp3\"", args);
    }

    [Fact]
    public void Tags_are_carried_across_and_only_audio_is_copied()
    {
        // Without -map 0:a an embedded cover becomes a video stream and lame refuses it;
        // without -map_metadata 0 the result has no artist or title and the scanner falls
        // back to filenames.
        var args = Transcoder.BuildArguments(@"C:\music\song.flac", @"C:\music\song.mp3.partial", "320k");

        Assert.Contains("-map 0:a", args);
        Assert.Contains("-map_metadata 0", args);
        Assert.Contains("-id3v2_version 3", args);
    }

    [Fact]
    public void The_configured_bitrate_is_used()
    {
        var args = Transcoder.BuildArguments(@"C:\music\song.flac", @"C:\music\out.mp3.partial", "192k");

        Assert.Contains("-b:a 192k", args);
    }

    [Fact]
    public void Paths_are_quoted_so_spaces_survive()
    {
        var args = Transcoder.BuildArguments(@"C:\my music\a song.flac", @"C:\my music\a song.mp3.partial", "320k");

        Assert.Contains("\"C:\\my music\\a song.flac\"", args);
        Assert.Contains("\"C:\\my music\\a song.mp3.partial\"", args);
    }

    [Fact]
    public async Task A_missing_ffmpeg_is_reported_rather_than_thrown()
    {
        var transcoder = Create(new TranscodeOptions { FfmpegPath = "mootify-no-such-ffmpeg" });

        var (ok, version) = await transcoder.ProbeAsync();

        Assert.False(ok);
        Assert.Null(version);
        Assert.False(transcoder.IsAvailable);
    }
}
