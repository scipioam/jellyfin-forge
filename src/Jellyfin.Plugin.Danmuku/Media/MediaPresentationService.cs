using System.Globalization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Danmuku.Media;

public sealed record MediaPresentation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("canBind")] bool CanBind,
    [property: JsonPropertyName("displayTitle")] string DisplayTitle,
    [property: JsonPropertyName("locationText")] string? LocationText,
    [property: JsonPropertyName("canNavigate")] bool CanNavigate);
public sealed record MediaLibraryLocation(Guid Id, string? Name, IReadOnlyList<string> Roots);

/// <summary>Request scoped: never retains library or title metadata across requests.</summary>
public sealed class MediaPresentationService(ILibraryManager library)
{
    private readonly Dictionary<Guid, BaseItem?> items = [];
    private IReadOnlyList<MediaLibraryLocation>? locations;

    public BaseItem? Find(Guid id)
    {
        if (id == Guid.Empty) return null;
        if (!items.TryGetValue(id, out var item))
        {
            try { item = library.GetItemById(id); }
            catch (Exception) { item = null; }
            items[id] = item;
        }
        return item;
    }

    public MediaPresentation Describe(Guid id) => Describe(Find(id), id);

    public MediaPresentation Describe(BaseItem? item, Guid id = default)
    {
        if (item is null) return new(id.ToString("N"), id.ToString("N"), "", false, id.ToString("N"), null, false);
        items[item.Id] = item;
        var series = item switch
        {
            Episode episode => Clean(episode.SeriesName) ?? Clean(Find(episode.SeriesId)?.Name),
            Season season => Clean(season.SeriesName) ?? Clean(Find(season.SeriesId)?.Name),
            _ => null
        };
        IReadOnlyList<MediaLibraryLocation> belonging = [];
        try
        {
            locations ??= ReadLocations();
            var folders = library.GetCollectionFolders(item);
            belonging = folders.Select(f => locations.FirstOrDefault(l => l.Id == f.Id)
                ?? new MediaLibraryLocation(f.Id, f.Name, [])).ToArray();
        }
        catch (Exception) { /* Metadata failure must not block management of existing references. */ }
        return new(item.Id.ToString("N"), item.Name ?? "", item.GetType().Name, MediaEligibility.CanBind(item),
            Title(item, series), Location(item, belonging), MediaEligibility.CanNavigate(item));
    }

    private IReadOnlyList<MediaLibraryLocation> ReadLocations()
    {
        try
        {
            // VirtualFolderInfo.Locations is alphabetically sorted by Jellyfin.
            // PathInfos retains configuration order, which defines our root labels.
            return library.GetVirtualFolders().Select(v => new MediaLibraryLocation(
                Guid.TryParse(v.ItemId, out var key) ? key : Guid.Empty, v.Name,
                v.LibraryOptions?.PathInfos is { Length: > 0 } paths ? paths.Select(p => p.Path).ToArray()
                    : v.Locations is { Length: 1 } single ? single : [])).ToArray();
        }
        catch (Exception) { return []; }
    }

    public static string Title(BaseItem item, string? seriesName = null)
    {
        var title = Clean(item.Name) ?? FileName(item.Path) ?? item.Id.ToString("N");
        if (item is Movie && item.ProductionYear is > 0) return $"{title}（{item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)}）";
        if (item is Season season) return string.Join(" · ", new[] { Clean(seriesName) ?? Clean(season.SeriesName), title }.Where(s => s is not null));
        if (item is not Episode episode) return title;
        var number = (episode.ParentIndexNumber is >= 0 ? "S" + episode.ParentIndexNumber.Value.ToString("D2", CultureInfo.InvariantCulture) : "")
            + (episode.IndexNumber is >= 0 ? "E" + episode.IndexNumber.Value.ToString("D2", CultureInfo.InvariantCulture) : "");
        return string.Join(" · ", new[] { Clean(seriesName) ?? Clean(episode.SeriesName), Clean(number), title }.Where(s => s is not null));
    }

    public static string Location(BaseItem item, IReadOnlyList<MediaLibraryLocation> libraries)
    {
        var id = item.Id.ToString("N");
        var file = FileName(item.Path);
        var path = LocalPath(item.Path);
        var ordered = libraries.GroupBy(l => l.Id).Select(g => g.First()).OrderBy(l => l.Id).ToArray();
        var names = ordered.GroupBy(l => Clean(l.Name)).ToDictionary(g => g.Key ?? "", g => g.Count());
        var output = new List<string>();
        foreach (var lib in ordered)
        {
            var name = Clean(lib.Name);
            if (name is not null && names[name] > 1) name += "（" + lib.Id.ToString("N") + "）";
            var roots = lib.Roots.Select(LocalPath).Where(r => r is not null).Cast<string>().Distinct(PathComparer).ToArray();
            var match = path is null ? null : roots.Where(r => IsWithin(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
            if (name is not null && match is not null)
            {
                var relative = Path.GetRelativePath(match, path!);
                if (relative != "." && !Path.IsPathRooted(relative) && !relative.Split(Path.DirectorySeparatorChar).Contains(".."))
                {
                    output.Add(string.Join(" / ", new[] { name, roots.Length > 1 ? "目录 " + (Array.IndexOf(roots, match) + 1).ToString(CultureInfo.InvariantCulture) : null,
                        relative.Replace(Path.DirectorySeparatorChar.ToString(), " / ") }.Where(s => s is not null)));
                    continue;
                }
            }
            output.Add(string.Join(" / ", new[] { name, file, id }.Where(s => s is not null)));
        }
        return output.Count == 0 ? string.Join(" / ", new[] { file, id }.Where(s => s is not null)) : string.Join("\n", output);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool IsWithin(string path, string root) => PathComparer.Equals(path, root)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsLocalFormat(string? path) => !string.IsNullOrWhiteSpace(path)
        && !path.Contains("://", StringComparison.Ordinal) && !path.Contains('\0')
        && (OperatingSystem.IsWindows() || (!path.Contains('\\') && !path.Contains(':')));
    private static string? FileName(string? path) => IsLocalFormat(path) ? Clean(Path.GetFileName(path)) : null;
    private static string? LocalPath(string? path)
    {
        if (!IsLocalFormat(path) || !Path.IsPathFullyQualified(path!)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
