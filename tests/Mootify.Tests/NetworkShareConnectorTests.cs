using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// The path handling, which is what gets a session opened against the wrong thing. The
/// P/Invoke itself isn't exercised here — it needs a real share and a real Windows box.
/// </summary>
public sealed class NetworkShareConnectorTests
{
    private static NetworkShareConnector Create(LibraryOptions options) =>
        new(new StaticOptionsMonitor<LibraryOptions>(options), NullLogger<NetworkShareConnector>.Instance);

    [Theory]
    [InlineData(@"\\192.168.1.3\Data\MediaMusic", true)]
    [InlineData(@"\\nas\music", true)]
    [InlineData("//nas/music", true)]
    [InlineData(@"D:\Music", false)]
    [InlineData("/mnt/music", false)]
    [InlineData("", false)]
    public void Unc_paths_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, NetworkShareConnector.IsUncPath(path));
    }

    [Theory]
    // The session is per share, so connecting to the deepest folder would be wrong.
    [InlineData(@"\\192.168.1.3\Data\MediaMusic", @"\\192.168.1.3\Data")]
    [InlineData(@"\\nas\music", @"\\nas\music")]
    [InlineData(@"\\nas\music\", @"\\nas\music")]
    [InlineData(@"\\nas\share\a\b\c\d", @"\\nas\share")]
    // Forward slashes are legal in UNC paths and people do write them.
    [InlineData("//nas/share/music", @"\\nas\share")]
    public void The_share_root_is_the_first_two_segments(string path, string expected)
    {
        Assert.Equal(expected, NetworkShareConnector.ShareRoot(path));
    }

    [Fact]
    public async Task An_unset_music_root_reports_that_rather_than_connecting()
    {
        var result = await Create(new LibraryOptions { MusicRoot = "" }).EnsureConnectedAsync();

        Assert.False(result.Connected);
        Assert.Contains("not set", result.Detail);
    }

    [Fact]
    public async Task A_local_path_that_exists_needs_no_session()
    {
        var result = await Create(new LibraryOptions { MusicRoot = Path.GetTempPath() }).EnsureConnectedAsync();

        Assert.True(result.Connected);
        Assert.Contains("without credentials", result.Detail);
    }

    [Fact]
    public async Task A_missing_local_path_is_not_treated_as_a_share()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mootify-not-here-" + Guid.NewGuid().ToString("n"));

        var result = await Create(new LibraryOptions { MusicRoot = missing }).EnsureConnectedAsync();

        Assert.False(result.Connected);
        Assert.Contains("does not exist", result.Detail);
    }

    [Fact]
    public async Task An_unreachable_share_without_credentials_says_so()
    {
        // The common misconfiguration: a share that needs a login, and no login given.
        var options = new LibraryOptions { MusicRoot = @"\\mootify-no-such-host\share\music" };

        var result = await Create(options).EnsureConnectedAsync();

        Assert.False(result.Connected);
        Assert.Contains("Library:Username", result.Detail);
    }

    [Fact]
    public void Credentials_are_only_considered_present_when_a_username_is_set()
    {
        Assert.False(new LibraryOptions().HasCredentials);
        Assert.False(new LibraryOptions { Password = "secret" }.HasCredentials);
        Assert.True(new LibraryOptions { Username = "devion" }.HasCredentials);
    }
}
