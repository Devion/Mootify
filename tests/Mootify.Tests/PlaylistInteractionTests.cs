using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Mootify.Components.Pages;
using Mootify.Components.Shared;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Notifications;
using Mootify.Services.Playback;
using Mootify.Services.Playlists;
using Mootify.Services.Recommendations;
using Mootify.Services.Settings;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class PlaylistInteractionTests
{
    private sealed class CaptureComponents : IComponentActivator
    {
        public InfiniteScroll Scroll { get; private set; } = null!;
        public TrackList Tracks { get; private set; } = null!;
        public IComponent CreateInstance(Type componentType)
        {
            var instance = (IComponent)Activator.CreateInstance(componentType)!;
            if (instance is InfiniteScroll scroll) Scroll = scroll;
            if (instance is TrackList tracks) Tracks = tracks;
            return instance;
        }
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/playlist");
        public string? LastUri { get; private set; }
        protected override void NavigateToCore(string uri, bool forceLoad) => LastUri = uri;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Infinite_playlist_keeps_numbering_and_reordered_items_visible(bool moveAtBoundary)
    {
        await using var db = new TestDatabase();
        var user = await db.AddUserAsync("listener");
        var ids = await db.AddTracksAsync(120);
        var playlists = new PlaylistService(db, NullLogger<PlaylistService>.Instance);
        var playlist = (await playlists.CreateAsync(user.Id, "Long playlist"))!.Value;
        await playlists.AddTracksAsync(playlist, user.Id, ids);
        var js = new FakeJsRuntime();
        var current = new CurrentUser(new FakeAuthStateProvider(user.Id));
        var listening = new ListeningService(db, playlists, NullLogger<ListeningService>.Instance);
        var notifications = new NotificationDispatcher(db, NullLogger<NotificationDispatcher>.Instance);
        await using var player = new PlayerService(js, db, listening,
            new TasteService(db, NullLogger<TasteService>.Instance), new PreferenceService(db), current,
            NullLogger<PlayerService>.Instance);
        var capture = new CaptureComponents();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IComponentActivator>(capture);
        services.AddSingleton<IJSRuntime>(js);
        var navigation = new Navigation();
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton(current);
        services.AddSingleton(player);
        services.AddSingleton(playlists);
        services.AddSingleton(listening);
        services.AddSingleton(new PlaylistEvents());
        services.AddSingleton(new TeamService(db, notifications, NullLogger<TeamService>.Instance));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<PlaylistPage>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(PlaylistPage.PlaylistId)] = playlist }));
            Assert.Equal(100, capture.Tracks.Rows.Count);
            Assert.Contains("tracklist__index\">100</span>", rendered.ToHtmlString());
            if (moveAtBoundary)
            {
                var item = capture.Tracks.Rows[99].ItemId!.Value;
                await capture.Tracks.OnMove.InvokeAsync(new TrackList.MoveRequest(item, 1));
                Assert.Equal(item, capture.Tracks.Rows[100].ItemId);
            }
            else await capture.Scroll.ReachedAsync();
            Assert.Equal(120, capture.Tracks.Rows.Count);
            var html = rendered.ToHtmlString();
            Assert.Contains("tracklist__index\">101</span>", html);
            Assert.Contains("tracklist__index\">120</span>", html);
            Assert.Contains("All 120 songs shown", html);
            Assert.DoesNotContain("class=\"pager", html);
            var last = capture.Tracks.Rows[119];
            await capture.Tracks.OnMove.InvokeAsync(new TrackList.MoveRequest(last.ItemId!.Value, 0, capture.Tracks.Rows[0].ItemId));
            Assert.Equal(120, capture.Tracks.Rows.Count);
            Assert.Equal(last.ItemId, capture.Tracks.Rows[0].ItemId);
            Assert.True(capture.Tracks.OnRequestReplacement.HasDelegate);
            await capture.Tracks.OnRequestReplacement.InvokeAsync(last);
            Assert.Equal($"/search?replaceItem={last.ItemId}", navigation.LastUri);
        });
    }
}
