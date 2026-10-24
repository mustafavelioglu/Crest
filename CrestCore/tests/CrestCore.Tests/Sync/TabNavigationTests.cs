using System.Text;
using System.Text.Json.Nodes;

using CrestCore.Application;
using CrestCore.Contracts;
using CrestCore.Domain;

using Xunit;

namespace CrestCore.Tests;

public sealed partial class BrowserContractsTests {
    #region Actions - Continuation tests

    [Fact]
    public void NavigationSurvivesStorageAndSyncWithoutSendingTheDevicesRetainedBranch() {
        var fixture = SavedSession();
        var source = fixture.Document["session"]!["spaces"]![0]!["tabs"]![0]!;
        var tab = StoredSessionCodec.DecodeTab(source);
        PageHistoryEntry[] entries = [Entry("https://a.example/"), Entry(tab.Url!), Entry("https://a.example/")];
        var shared = TabNavigation.Capture(entries, 1)! with { Clock = 7, DeviceId = Guid.NewGuid() };
        var local = TabNavigation.Capture([Entry("https://local.example/")], 0)!;
        tab = tab with { Navigation = shared, LocalNavigation = local };
        var restored = StoredSessionCodec.DecodeTab(StoredSessionCodec.Encode(tab));
        Assert.True(shared.SamePath(restored.Navigation!));
        Assert.True(local.SamePath(restored.LocalNavigation!));

        fixture.Document["session"]!["spaces"]![0]!["tabs"]![0] = StoredSessionCodec.Encode(tab);
        var projected = NativeSyncProjection.Project(fixture.Document["session"]!.AsObject(), SyncProjectionPreferences(), []);
        var payload = projected.Single(item => item!["type"]!.GetValue<string>() == "tab")!;
        Assert.Null(payload["value"]!["localNavigation"]);
        var body = SyncRecordBody.Read(payload, false, SyncPayloadForm.Journal);
        Assert.Equal(4, body.Schema);
        var received = Assert.IsType<TabPayload>(SyncRecordBody.Read(body.Bytes(SyncPayloadForm.Cloud), false, SyncPayloadForm.Cloud).Payload);
        Assert.True(shared.SamePath(received.Navigation!));
        var archived = new ArchivePayload(received with { Placement = TabPlacement.Current, FolderId = null, SplitGroupId = null },
            new SyncTime(800000010), ArchiveReason.Closed);
        Assert.Equal(4, archived.Schema);
        Assert.True(shared.SamePath(Assert.IsType<ArchivePayload>(SyncPayload.Decode(archived.Encode(SyncPayloadForm.Journal),
            SyncPayloadForm.Journal)).Tab.Navigation!));
    }

    [Fact]
    public void ATabRenameCannotWinOverANewerNavigationAndConflictsConverge() {
        var first = SyncTabRecord(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        var second = first.DeepClone().AsObject();
        var device = Guid.NewGuid();
        var a = TabNavigation.Capture([Entry("https://a.example/")], 0)! with { Clock = 8, DeviceId = device };
        var b = TabNavigation.Capture([Entry("https://b.example/")], 0)! with { Clock = 7, DeviceId = device };
        first["payload"]!["value"]!["navigation"] = a.Encode();
        first["payload"]!["value"]!["url"] = a.Current.Url;
        second["payload"]!["value"]!["navigation"] = b.Encode();
        second["payload"]!["value"]!["url"] = b.Current.Url;
        second["version"]!["logicalClock"] = 100UL;
        second["payload"]!["value"]!["customTitle"] = "Renamed";
        second["payload"]!["value"]!["titleModifiedAt"] = 99.0;
        var merged = NativeSyncEvaluator.Resolve(first, second);
        Assert.Equal(a.Current.Url, merged["payload"]!["value"]!["url"]!.GetValue<string>());
        Assert.Equal("Renamed", merged["payload"]!["value"]!["customTitle"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(merged, NativeSyncEvaluator.Resolve(second, first)));
        Assert.True(JsonNode.DeepEquals(merged, NativeSyncEvaluator.Resolve(merged, second)));
        // Equal navigation clocks use device identity, independent of a
        // newer rename on the device whose navigation loses.
        first["version"]!["logicalClock"] = 101UL;
        first["payload"]!["value"]!["navigation"] = (a with { DeviceId = Guid.Parse("00000000-0000-4000-8000-000000000001") }).Encode();
        second["payload"]!["value"]!["navigation"] = (b with { Clock = 8, DeviceId = Guid.Parse("00000000-0000-4000-8000-000000000002") }).Encode();
        merged = NativeSyncEvaluator.Resolve(first, second);
        Assert.Equal(b.Current.Url, merged["payload"]!["value"]!["url"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(merged, NativeSyncEvaluator.Resolve(second, first)));
    }

    [Fact]
    public void ImportedHistoryKeepsBothDirectionsAndDoesNotPublishItsInitialLoad() {
        PageHistoryEntry[] entries = [Entry("https://a.example/"), Entry("https://b.example/"), Entry("https://a.example/")];
        var snapshot = TabNavigation.Capture(entries, 1)!;
        var history = PageHistory.Continue(snapshot, EngineKind.WebKit);
        history.Reopen(EngineKind.WebKit, restores: false);
        var native = entries[1] with { Id = Guid.NewGuid() };
        history.Report(EngineKind.WebKit, [native], 0);
        Assert.Equal([entries[0]], history.Back);
        Assert.Equal([entries[2]], history.Forward);
        Assert.NotNull(history.ChangedNavigation(out bool publishes));
        Assert.False(publishes);
        Assert.Null(history.ChangedNavigation(out _));

        var next = Entry("https://d.example/");
        history.Report(EngineKind.WebKit, [native, next], 1);
        Assert.Empty(history.Forward);
        Assert.Equal([native, entries[0]], history.Back);
        Assert.NotNull(history.ChangedNavigation(out publishes));
        Assert.True(publishes);
    }

    [Fact]
    public void PortableHistoryIsBoundedAndRejectsMalformedOrMismatchedSnapshots() {
        var entries = Enumerable.Range(0, 100).Select(index => Entry("https://example.com/" + index + "?" + new string('x', 7000))).ToArray();
        var snapshot = TabNavigation.Capture(entries, 50)!;
        Assert.Equal(entries[50].Id, snapshot.CurrentEntryId);
        Assert.InRange(snapshot.Entries.Count, 1, 100);
        Assert.True(Encoding.UTF8.GetByteCount(snapshot.Encode().ToJsonString()) <= TabNavigation.MaximumBytes);
        Assert.NotNull(TabNavigation.Decode(snapshot.Encode(), snapshot.Current.Url));
        Assert.Null(TabNavigation.Decode(snapshot.Encode(), "https://other.example/"));
        var broken = snapshot.Encode();
        broken["currentEntryID"] = Guid.NewGuid().ToString();
        Assert.Null(TabNavigation.Decode(broken, snapshot.Current.Url));
        broken = snapshot.Encode();
        broken["entries"]!.AsArray().Add(broken["entries"]![0]!.DeepClone());
        Assert.Null(TabNavigation.Decode(broken, snapshot.Current.Url));
        Assert.Null(TabNavigation.Decode(new JsonObject(), snapshot.Current.Url));
        Assert.Null(TabNavigation.Capture([Entry("file:///private/local")], 0));
        var unicode = TabNavigation.Capture([Entry("https://example.com/") with { Title = string.Concat(Enumerable.Repeat("🌍", 5000)) }], 0)!;
        Assert.Equal(2048, new UTF8Encoding(false, true).GetByteCount(unicode.Current.Title));
        Assert.NotNull(TabNavigation.Decode(unicode.Encode(), unicode.Current.Url));
    }

    [Fact]
    public void ARemoteContinuationLeavesTheResidentPageAndItsRetainedSessionUndisturbed() {
        var fixture = SavedSession();
        var session = fixture.Document["session"]!.AsObject();
        session.Remove("disposableSeedMarker");
        var (app, engine, binding, workspace, window) = PageHost(session);
        using var disposal = app;
        var (space, tab, page) = (fixture.Space, fixture.Tab, Guid.NewGuid());
        app.Send(new OpenPage(page, workspace, space, tab, window));
        app.Report(engine, new PageCreated(page));
        var owner = app.Workspace(workspace);
        var sync = Syncing(owner);
        PageHistoryEntry[] local = [Entry("https://local.example/a"), Entry("https://local.example/b")];
        app.Report(engine, new PageHistoryReported(page, local, 1));
        app.Drain();
        var initial = owner.Current.Spaces.Single(held => held.Id == space).Tabs.Single(held => held.Id == tab).Navigation!;
        var remote = TabNavigation.Capture([Entry("https://remote.example/a"), Entry("https://remote.example/b")], 1)!
            with { Clock = initial.Clock + 10, DeviceId = Guid.NewGuid() };
        var record = CloudTab(tab, space, 10000);
        var body = JsonNode.Parse(record.Body)!;
        body["value"]!["url"] = remote.Current.Url;
        body["value"]!["navigation"] = remote.Encode();
        record = record with { Schema = 4, Body = Bytes(body) };
        int commands = binding.Commands.Count;

        owner.Handle(new MergeSyncRecords([record]), DateTimeOffset.UtcNow, new TestIds(), new Lock());

        var accepted = owner.Current.Spaces.Single(held => held.Id == space).Tabs.Single(held => held.Id == tab);
        Assert.True(remote.SamePath(accepted.Navigation!));
        Assert.True(initial.SamePath(accepted.LocalNavigation!));
        Assert.Equal(commands, binding.Commands.Count);
        Assert.Equal([local[0]], app.Query(new PageHistoryEntries(page)).Back);
        // A duplicate native report does not publish the resident branch back.
        app.Report(engine, new PageHistoryReported(page, local, 1));
        Assert.Equal(remote, owner.Current.Spaces.Single(held => held.Id == space).Tabs.Single(held => held.Id == tab).Navigation);

        // Unloading resumes the local branch rather than navigating remotely.
        app.Send(new ReleasePage(page, KeepsState: true));
        var reopened = Guid.NewGuid();
        app.Send(new OpenPage(reopened, workspace, space, tab, window));
        app.Report(engine, new PageCreated(reopened));
        Assert.Equal(local[1].Url, Assert.IsType<LoadPage>(binding.Commands[^1]).Url);
        sync.Flush();
        var upload = sync.Snapshot.Uploading(new(SyncRecordKind.Tab, tab))!;
        Assert.DoesNotContain("localNavigation", Encoding.UTF8.GetString(upload.Body));
    }

    [Fact]
    public void FreshPagesContinueSharedHistoryAndClearingVisitsLeavesTheirSequenceAlone() {
        var fixture = SavedSession();
        PageHistoryEntry[] entries = [Entry("https://a.example/"), Entry("https://b.example/"), Entry("https://c.example/")];
        var navigation = TabNavigation.Capture(entries, 1)! with { Clock = 8, DeviceId = Guid.NewGuid() };
        var tab = fixture.Document["session"]!["spaces"]![0]!["tabs"]![0]!;
        tab["url"] = navigation.Current.Url;
        tab["navigation"] = navigation.Encode();
        var (app, engine, binding, workspace, window) = PageHost(fixture.Document["session"]!);
        using var disposal = app;
        var page = Guid.NewGuid();
        app.Send(new OpenPage(page, workspace, fixture.Space, fixture.Tab, window));
        Assert.Equal(navigation.Current.Url, app.Pages.Hosted(page)!.Live.PendingUrl);
        app.Report(engine, new PageCreated(page));
        Assert.Equal(navigation.Current.Url, Assert.IsType<LoadPage>(binding.Commands[^1]).Url);
        var native = entries[1] with { Id = Guid.NewGuid() };
        app.Report(engine, new PageHistoryReported(page, [native], 0));
        var current = app.Workspace(workspace).Current.Spaces.Single(held => held.Id == fixture.Space);
        Assert.Equal(navigation, current.Tabs.Single(held => held.Id == fixture.Tab).Navigation);
        app.Send(new ClearHistory(workspace, fixture.Space));
        Assert.Empty(app.Workspace(workspace).Current.Spaces.Single(held => held.Id == fixture.Space).History);
        Assert.Equal([entries[0]], app.Query(new PageHistoryEntries(page)).Back);
        Assert.Equal([entries[2]], app.Query(new PageHistoryEntries(page)).Forward);
    }

    [Fact]
    public void PrivatePagesNeverPersistPortableHistory() {
        var fixture = SavedSession();
        var (app, engine, _, workspace, window) = PageHost(fixture.Document["session"]!, WorkspaceKind.Private);
        using var disposal = app;
        var page = Guid.NewGuid();
        var space = app.Workspace(workspace).Current.Spaces[0];
        var tab = space.Tabs[0];
        app.Send(new OpenPage(page, workspace, space.Id, tab.Id, window));
        app.Report(engine, new PageCreated(page));
        app.Report(engine, new PageHistoryReported(page, [Entry("https://private.example/")], 0));
        var retained = app.Workspace(workspace).Current.Spaces[0].Tabs[0];
        Assert.Null(retained.Navigation);
        Assert.Null(retained.LocalNavigation);
    }

    [Fact]
    public void UnsupportedNavigationClearsRetainedPathsWithoutResettingTheLocalClock() {
        var (app, engine, _, page, workspace, _, space, tab) = LivePage();
        using var disposal = app;
        var a = Entry("https://a.example/");
        app.Report(engine, new PageHistoryReported(page, [a], 0));
        TabState Held() => app.Workspace(workspace).Current.Spaces.Single(held => held.Id == space).Tabs.Single(held => held.Id == tab);
        ulong first = Held().Navigation!.Clock;
        app.Report(engine, new PageHistoryReported(page, [a, Entry("file:///private/document")], 1));
        Assert.Null(Held().Navigation);
        Assert.Null(Held().LocalNavigation);
        var restored = StoredSessionCodec.DecodeTab(StoredSessionCodec.Encode(Held()));
        Assert.Equal(first, restored.NavigationClock);
        app.Report(engine, new PageHistoryReported(page, [Entry("https://next.example/")], 0));
        Assert.True(Held().Navigation!.Clock > first);
    }

    [Fact]
    public void LargeCollectionsOmitOptionalSnapshotsWithoutExhaustingTheCheckpointOrDroppingTabs() {
        var fixture = SavedSession();
        var source = StoredSessionCodec.DecodeSession(fixture.Document["session"]);
        var entries = Enumerable.Range(0, 100).Select(index => Entry("https://example.com/" + index + "?" + new string('x', 7000))).ToArray();
        var navigation = TabNavigation.Capture(entries, 50)! with { Clock = 1, DeviceId = Guid.NewGuid() };
        var template = source.Spaces[0].Tabs[0] with {
            Url = navigation.Current.Url,
            SavedUrl = null,
            Placement = TabPlacement.Current,
            Navigation = navigation,
            LocalNavigation = navigation
        };
        var tabs = Enumerable.Range(0, 350).Select(_ => template with { Id = Guid.NewGuid() }).ToArray();
        source = source with { Spaces = [source.Spaces[0] with { Tabs = tabs, ArchivedTabs = [], History = [] }] };
        var checkpoint = new NativeSessionCheckpoint(source).Core();
        Assert.True(checkpoint.Length < NativeSessionAuthority.MaximumBytes);
        var stored = JsonNode.Parse(checkpoint)!["spaces"]![0]!["tabs"]!.AsArray();
        Assert.Equal(tabs.Length, stored.Count);
        Assert.Contains(stored, tab => tab!["navigation"] is null);
        Assert.All(stored, tab => Assert.Equal(navigation.Current.Url, tab!["url"]!.GetValue<string>()));
        foreach (var field in new[] { "navigation", "localNavigation" }) {
            long bytes = stored.Sum(tab => tab![field] is { } value ? Encoding.UTF8.GetByteCount(value.ToJsonString()) : 0);
            Assert.InRange(bytes, 1, TabNavigation.Budget.MaximumBytes);
        }
    }

    [Fact]
    public void ArchivingAndReopeningRetainsThePortableSequence() {
        var fixture = SavedSession();
        var tab = fixture.Document["session"]!["spaces"]![0]!["tabs"]![0]!;
        var entries = new[] { Entry("https://a.example/"), Entry("https://b.example/"), Entry("https://c.example/") };
        var navigation = TabNavigation.Capture(entries, 1)! with { Clock = 3, DeviceId = Guid.NewGuid() };
        tab["url"] = navigation.Current.Url;
        tab["navigation"] = navigation.Encode();
        tab["placement"] = "current";
        tab.AsObject().Remove("folderID");
        tab.AsObject().Remove("splitGroupID");
        var (app, engine, _, workspace, window) = PageHost(fixture.Document["session"]!);
        using var disposal = app;
        app.Send(new CloseTab(workspace, window, fixture.Space, fixture.Tab));
        var archived = app.Workspace(workspace).Current.Spaces.Single(held => held.Id == fixture.Space).ArchivedTabs;
        Assert.True(navigation.SamePath(archived.Single(held => held.Tab.Id == fixture.Tab).Tab.Navigation!));
        app.Send(new RestoreArchivedTab(workspace, window, fixture.Space, fixture.Tab));
        var page = Guid.NewGuid();
        app.Send(new OpenPage(page, workspace, fixture.Space, fixture.Tab, window));
        app.Report(engine, new PageCreated(page));
        app.Report(engine, new PageHistoryReported(page, [entries[1] with { Id = Guid.NewGuid() }], 0));
        Assert.Equal([entries[0]], app.Query(new PageHistoryEntries(page)).Back);
        Assert.Equal([entries[2]], app.Query(new PageHistoryEntries(page)).Forward);
    }

    [Fact]
    public void ReturningASavedTabRetainsRemoteClockKnowledgeForItsNextNavigation() {
        var fixture = SavedSession();
        var state = StoredSessionCodec.DecodeTab(fixture.Document["session"]!["spaces"]![0]!["tabs"]![0]);
        var remote = TabNavigation.Capture([Entry("https://remote.example/")], 0)! with { Clock = 20, DeviceId = Guid.NewGuid() };
        var tab = BrowserTab.Restore(state with { Url = remote.Current.Url, Navigation = remote, NavigationClock = 1 });
        tab.ReturnToSavedAddress();
        Assert.Null(tab.State.Navigation);
        Assert.Null(tab.State.LocalNavigation);
        var restored = StoredSessionCodec.DecodeTab(StoredSessionCodec.Encode(tab.State));
        Assert.Equal(20UL, restored.NavigationClock);
        var space = StoredSessionCodec.DecodeSpace(fixture.Document["session"]!["spaces"]![0]) with { Tabs = [restored] };
        var next = TabNavigation.Capture([Entry("https://next.example/")], 0)!;
        var edited = new NavigationHistoryRecord(Guid.NewGuid(), space.Id, DateTimeOffset.UtcNow, restored.Id,
            Guid.NewGuid(), next, Publishes: true).Apply(space, DateTimeOffset.UtcNow)!.Value.Space;
        Assert.Equal(21UL, edited.Tabs[0].Navigation!.Clock);
    }

    #endregion
}
