using System.Text;
using System.Text.Json.Nodes;

using CrestCore.Contracts;

namespace CrestCore.Domain;

/// Portable continuation of one tab. Entries belong to this sequence, rather
/// than to the Space's aggregate visit history. Engine state never travels.
internal sealed record TabNavigation(ulong Clock, Guid DeviceId, IReadOnlyList<PageHistoryEntry> Entries, Guid CurrentEntryId) {
    #region Types

    /// Session encoding reserves separate space for shared continuations and
    /// retained local sessions, so optional history cannot exhaust the core
    /// checkpoint or journal on its own. Whole snapshots are omitted at the
    /// budget; their tab records and current URLs remain readable.
    internal sealed class Budget {
        #region Static Variables

        internal const int MaximumBytes = 16 * 1024 * 1024;

        #endregion

        #region Variables

        private int remaining = MaximumBytes;

        #endregion

        #region Actions - Storage

        internal bool Includes(TabNavigation navigation) {
            if (navigation.reservedBytes > remaining) return false;
            remaining -= navigation.reservedBytes;
            return true;
        }

        #endregion
    }

    #endregion

    #region Static Variables

    internal const int MaximumEntries = 100;
    internal const int MaximumBytes = 64 * 1024;
    private const int MaximumTitleBytes = 2_048;

    #endregion

    #region Variables

    private readonly int reservedBytes = 256 + Entries.Sum(EntryBytes);

    internal PageHistoryEntry Current => Entries.First(entry => entry.Id == CurrentEntryId);

    #endregion

    #region Actions - Navigation

    internal bool Matches(string? url) => url is not null && (new SyncedAddress(Current.Url).Spelled ?? Current.Url) == (new SyncedAddress(url).Spelled ?? url);

    internal bool SamePath(TabNavigation other) => CurrentEntryId == other.CurrentEntryId && Entries.SequenceEqual(other.Entries);

    internal int CompareTo(TabNavigation other) => new SyncVersion(Clock, DeviceId).CompareTo(new(other.Clock, other.DeviceId));

    /// Keeps a contiguous portable stretch around the shown entry, bounded by
    /// encoded bytes as well as count. An unsupported current URL keeps none.
    internal static TabNavigation? Capture(IReadOnlyList<PageHistoryEntry> entries, int current) {
        if (current < 0 || current >= entries.Count || !SyncedAddress.Includes(entries[current].Url)) return null;
        var fitted = entries.Select(entry => new PageHistoryEntry(entry.Id,
            new SyncedAddress(entry.Url).Spelled ?? entry.Url, FitTitle(entry.Title))).ToArray();
        int first = current, last = current;
        int bytes = EntryBytes(fitted[current]) + 256;
        // Prefer nearby back entries, while leaving room for forward entries.
        bool back = true, forward = true;
        while (last - first + 1 < MaximumEntries && (back || forward)) {
            if (back) back = first > 0 && Take(first - 1);
            if (back) first--;
            if (last - first + 1 == MaximumEntries) break;
            if (forward) forward = last + 1 < fitted.Length && Take(last + 1);
            if (forward) last++;
        }
        return new(0, Guid.Empty, fitted[first..(last + 1)], fitted[current].Id);

        bool Take(int index) {
            if (!SyncedAddress.Includes(fitted[index].Url)) return false;
            int size = EntryBytes(fitted[index]);
            if (bytes + size > MaximumBytes) return false;
            bytes += size;
            return true;
        }
    }

    private static string FitTitle(string title) {
        if (Encoding.UTF8.GetByteCount(title) <= MaximumTitleBytes) return title;
        int length = 0, bytes = 0;
        foreach (var rune in title.EnumerateRunes()) {
            if (bytes + rune.Utf8SequenceLength > MaximumTitleBytes) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return title[..length];
    }

    #endregion

    #region Actions - Equality

    public bool Equals(TabNavigation? other) => other is not null && Clock == other.Clock && DeviceId == other.DeviceId && SamePath(other);

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Clock);
        hash.Add(DeviceId);
        hash.Add(CurrentEntryId);
        foreach (var entry in Entries) hash.Add(entry);
        return hash.ToHashCode();
    }

    #endregion

    #region Actions - Coding

    internal JsonObject Encode() => new() {
        ["version"] = 1,
        ["clock"] = Clock,
        ["deviceID"] = DeviceId.ToString("D"),
        ["currentEntryID"] = CurrentEntryId.ToString("D"),
        ["entries"] = new JsonArray(Entries.Select(entry => (JsonNode)EncodeEntry(entry)).ToArray())
    };

    /// An invalid optional continuation is ignored; it never hides its tab.
    internal static TabNavigation? Decode(JsonNode? node, string? url, bool requiresMatchingUrl = true) {
        if (node is null) return null;
        try {
            var value = node.AsObject();
            if (value["version"]!.GetValue<int>() != 1 || Encoding.UTF8.GetByteCount(node.ToJsonString()) > MaximumBytes) return null;
            var entries = value["entries"]!.AsArray();
            if (entries.Count is < 1 or > MaximumEntries) return null;
            var read = entries.Select(entry => {
                var item = entry!.AsObject();
                return new PageHistoryEntry(Guid.ParseExact(item["id"]!.GetValue<string>(), "D"), item["url"]!.GetValue<string>(), item["title"]!.GetValue<string>());
            }).ToArray();
            if (read.Any(entry => entry.Id == Guid.Empty || !new SyncedAddress(entry.Url).IsPortable
                || Encoding.UTF8.GetByteCount(entry.Title) > MaximumTitleBytes)
                || read.Select(entry => entry.Id).Distinct().Count() != read.Length) return null;
            var navigation = new TabNavigation(value["clock"]!.GetValue<ulong>(), Guid.ParseExact(value["deviceID"]!.GetValue<string>(), "D"), read,
                Guid.ParseExact(value["currentEntryID"]!.GetValue<string>(), "D"));
            return read.Any(entry => entry.Id == navigation.CurrentEntryId) && (!requiresMatchingUrl || navigation.Matches(url)) ? navigation : null;
        } catch (Exception error) when (error is InvalidOperationException or FormatException or NullReferenceException or System.Text.Json.JsonException
            or OverflowException or ArgumentException) {
            return null;
        }
    }

    private static JsonObject EncodeEntry(PageHistoryEntry entry) => new() {
        ["id"] = entry.Id.ToString("D"),
        ["url"] = entry.Url,
        ["title"] = entry.Title
    };

    private static int EntryBytes(PageHistoryEntry entry) => Encoding.UTF8.GetByteCount(EncodeEntry(entry).ToJsonString()) + 1;

    #endregion
}
