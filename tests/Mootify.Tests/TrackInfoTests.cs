using Mootify.Services.Playback;

namespace Mootify.Tests;

/// <summary>
/// The format badge in the play bar. Derived from the extension, because the path itself
/// must never reach the browser.
/// </summary>
public sealed class TrackInfoTests
{
    [Theory]
    [InlineData(@"C:\music\song.flac", "FLAC")]
    [InlineData(@"C:\music\song.FLAC", "FLAC")]
    [InlineData(@"C:\music\song.mp3", "MP3")]
    [InlineData(@"\\nas\music\a band\song.mp3", "MP3")]
    [InlineData(@"C:\music\song.ogg", "OGG")]
    [InlineData(@"C:\music\song", "AUDIO")]
    public void The_badge_comes_from_the_extension(string path, string expected)
    {
        Assert.Equal(expected, TrackInfo.FormatOf(path));
    }

    [Fact]
    public void A_dot_in_the_folder_name_does_not_confuse_it()
    {
        Assert.Equal("FLAC", TrackInfo.FormatOf(@"C:\music\A.C.T\song.flac"));
    }
}
