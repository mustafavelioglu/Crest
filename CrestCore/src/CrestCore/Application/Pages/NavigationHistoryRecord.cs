using CrestCore.Contracts;
using CrestCore.Domain;

namespace CrestCore.Application;

/// A tab's portable path changed locally. Its own revision is independent of
/// tab activation, appearance, ordering and ordinary record versions.
internal sealed record NavigationHistoryRecord(Guid PageId, Guid SpaceId, DateTimeOffset At, Guid TabId,
    Guid DeviceId, TabNavigation? Navigation, bool Publishes) : PageEdit(PageId, SpaceId, At) {
    #region Variables

    public override SyncStaging Staging => Publishes ? SyncStaging.Navigation : SyncStaging.PageReport;

    #endregion

    #region Actions - Editing

    public override (SpaceState Space, SessionFaviconUpdate? Favicon)? Apply(SpaceState space, DateTimeOffset now) {
        int index = IndexOf(space, TabId);
        if (index < 0) return null;
        var tab = space.Tabs[index];
        if (Navigation is null) {
            if (tab.Navigation is null && tab.LocalNavigation is null) return (space, null);
            var cleared = tab with {
                Navigation = null,
                LocalNavigation = null,
                NavigationClock = tab.KnownNavigationClock
            };
            return (space with { Tabs = [.. space.Tabs.Select((held, position) => position == index ? cleared : held)] }, null);
        }
        if (!Publishes) {
            if (tab.LocalNavigation?.SamePath(Navigation) == true) return (space, null);
            var retained = tab with { LocalNavigation = Navigation };
            return (space with { Tabs = [.. space.Tabs.Select((held, position) => position == index ? retained : held)] }, null);
        }
        ulong clock = tab.KnownNavigationClock;
        if (clock == ulong.MaxValue) return (space, null);
        var navigation = Navigation with { Clock = clock + 1, DeviceId = DeviceId };
        var next = tab with {
            Navigation = navigation,
            NavigationClock = navigation.Clock,
            LocalNavigation = navigation,
            Url = navigation.Current.Url,
            NativeContent = null,
            Title = navigation.Current.Title
        };
        return (space with { Tabs = [.. space.Tabs.Select((held, position) => position == index ? next : held)] }, null);
    }

    public override IEnumerable<Change> Announced(Guid workspaceId) => [];

    #endregion
}
