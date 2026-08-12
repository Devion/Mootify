using Mootify.Services.Import;

namespace Mootify.Tests;

/// <summary>
/// Grouping the missing songs before asking Lidarr for anything. This is what makes
/// "request all" affordable: Lidarr fetches whole albums, so six songs from one album is
/// one lookup and one download, not six.
/// </summary>
public sealed class ImportRequestQueueTests
{
    private static ImportedTrack Track(string title, string album, string artist) =>
        new(title, album, artist, TimeSpan.FromMinutes(3)) { AllArtists = [artist] };

    [Fact]
    public void Songs_from_one_album_count_as_one_album()
    {
        var missing = new[]
        {
            Track("Numb", "Meteora", "Linkin Park"),
            Track("Faint", "Meteora", "Linkin Park"),
            Track("Somewhere I Belong", "Meteora", "Linkin Park"),
        };

        Assert.Equal(1, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void Different_albums_stay_separate()
    {
        var missing = new[]
        {
            Track("Numb", "Meteora", "Linkin Park"),
            Track("In the End", "Hybrid Theory", "Linkin Park"),
            Track("Smells Like Teen Spirit", "Nevermind", "Nirvana"),
        };

        Assert.Equal(3, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void The_same_album_title_by_different_artists_is_two_albums()
    {
        // "Greatest Hits" is not one album.
        var missing = new[]
        {
            Track("A", "Greatest Hits", "Queen"),
            Track("B", "Greatest Hits", "ABBA"),
        };

        Assert.Equal(2, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void Album_names_group_regardless_of_case()
    {
        var missing = new[]
        {
            Track("A", "Meteora", "Linkin Park"),
            Track("B", "METEORA", "linkin park"),
        };

        Assert.Equal(1, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void Songs_with_no_album_are_asked_for_individually()
    {
        // Singles export with an empty album; grouping them together would request one
        // arbitrary track and drop the rest.
        var missing = new[]
        {
            Track("Single One", "", "Artist"),
            Track("Single Two", "", "Artist"),
        };

        Assert.Equal(2, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void A_real_shaped_export_collapses_to_far_fewer_albums()
    {
        // The point of the whole exercise: the album count is what the request step costs.
        var missing = Enumerable.Range(0, 60)
            .Select(i => Track($"Song {i}", $"Album {i / 6}", "Artist"))
            .ToList();

        Assert.Equal(10, ImportRequestQueue.CountAlbums(missing));
    }

    [Fact]
    public void Nothing_missing_means_nothing_to_request()
    {
        Assert.Equal(0, ImportRequestQueue.CountAlbums([]));
    }

    [Fact]
    public void Progress_percent_tracks_albums_not_songs()
    {
        var progress = new ImportRequestProgress(true, 200, 50, 130, 4, null, null, null);

        Assert.Equal(25, progress.Percent);
        Assert.Equal(0, ImportRequestProgress.Idle.Percent);
    }
}
