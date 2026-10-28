using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Danmuku.Media;

public static class MediaEligibility
{
    public static bool CanBind(BaseItem? item) => item is not null
        && (item.GetType() == typeof(Movie) || item.GetType() == typeof(Episode) || item.GetType() == typeof(Video))
        && item.SourceType == SourceType.Library && item.ExtraType is null && item.OwnerId == Guid.Empty;

    public static bool CanNavigate(BaseItem? item) => item is Series or Season;
}
