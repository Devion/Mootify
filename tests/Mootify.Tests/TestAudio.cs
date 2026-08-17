namespace Mootify.Tests;

/// <summary>
/// Real, if extremely short, MP3 files. Most library tests get away with 512 zero bytes,
/// because a file TagLib can't parse is itself the case under test — but anything that turns
/// on what the tags *say* needs a file TagLib will open and let a test write to.
/// </summary>
public static class TestAudio
{
    /// <summary>
    /// One silent MPEG-1 Layer III frame. 0xFF 0xFB 0x90 is 128kbps at 44.1kHz, which makes
    /// the frame 417 bytes; that's the least that will parse as audio.
    /// </summary>
    public static byte[] SilentMp3()
    {
        var bytes = new byte[417];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = 0x90;
        return bytes;
    }

    /// <summary>Writes a playable file and tags it. Any argument left null is simply not written.</summary>
    public static void Write(string path, string? artist = null, string? album = null, string? title = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SilentMp3());

        if (artist is null && album is null && title is null) return;

        using var tag = TagLib.File.Create(path);
        if (artist is not null) tag.Tag.Performers = [artist];
        if (album is not null) tag.Tag.Album = album;
        if (title is not null) tag.Tag.Title = title;
        tag.Save();
    }
}
