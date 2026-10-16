using System;
using System.Globalization;
using System.Text;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 生成与 Jellyfin 核心完全一致的 trickplay HLS 播放列表（.m3u8）。
/// 逻辑移植自 Jellyfin.Server.Implementations.Trickplay.TrickplayManager.GetHlsPlaylist。
/// </summary>
public static class HlsPlaylistBuilder
{
    /// <summary>
    /// 构造播放列表。
    /// </summary>
    /// <param name="mediaSourceId">Media source id（strm 场景下即 item id）。</param>
    /// <param name="info">Trickplay info.</param>
    /// <param name="apiKey">Requester api token.</param>
    /// <returns>Playlist content or null when not applicable.</returns>
    public static string? Build(Guid mediaSourceId, TrickplayInfo info, string? apiKey)
    {
        if (info.ThumbnailCount <= 0 || info.TileWidth <= 0 || info.TileHeight <= 0 || info.Interval <= 0)
        {
            return null;
        }

        const string urlFormat = "{0}.jpg?MediaSourceId={1}&ApiKey={2}";
        const string decimalFormat = "{0:0.###}";

        var builder = new StringBuilder(128);

        var resolution = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", info.Width, info.Height);
        var layout = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", info.TileWidth, info.TileHeight);
        var thumbnailsPerTile = info.TileWidth * info.TileHeight;
        var thumbnailDuration = info.Interval / 1000d;
        var infDuration = thumbnailDuration * thumbnailsPerTile;
        var tileCount = (int)Math.Ceiling((decimal)info.ThumbnailCount / thumbnailsPerTile);

        builder
            .AppendLine("#EXTM3U")
            .Append("#EXT-X-TARGETDURATION:")
            .AppendLine(tileCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine("#EXT-X-VERSION:7")
            .AppendLine("#EXT-X-MEDIA-SEQUENCE:1")
            .AppendLine("#EXT-X-PLAYLIST-TYPE:VOD")
            .AppendLine("#EXT-X-IMAGES-ONLY");

        for (var i = 0; i < tileCount; i++)
        {
            if (i == tileCount - 1)
            {
                thumbnailsPerTile = info.ThumbnailCount - (i * thumbnailsPerTile);
                infDuration = thumbnailDuration * thumbnailsPerTile;
            }

            builder
                .Append("#EXTINF:")
                .AppendFormat(CultureInfo.InvariantCulture, decimalFormat, infDuration)
                .AppendLine(",");

            builder
                .Append("#EXT-X-TILES:RESOLUTION=")
                .Append(resolution)
                .Append(",LAYOUT=")
                .Append(layout)
                .Append(",DURATION=")
                .AppendFormat(CultureInfo.InvariantCulture, decimalFormat, thumbnailDuration)
                .AppendLine();

            builder
                .AppendFormat(
                    CultureInfo.InvariantCulture,
                    urlFormat,
                    i.ToString(CultureInfo.InvariantCulture),
                    mediaSourceId.ToString("N"),
                    apiKey)
                .AppendLine();
        }

        builder.AppendLine("#EXT-X-ENDLIST");
        return builder.ToString();
    }
}
