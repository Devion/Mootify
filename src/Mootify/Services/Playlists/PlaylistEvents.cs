namespace Mootify.Services.Playlists;

/// <summary>
/// Lets a page tell the sidebar its playlists changed. Scoped, so one circuit's edits
/// don't ripple into another user's session — a static event here would leak across users
/// and outlive every circuit that subscribed to it.
/// </summary>
public sealed class PlaylistEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
