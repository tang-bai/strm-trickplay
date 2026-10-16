using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 一套磁盘上已存在的 trickplay 瓦片（对应一个 `&lt;width&gt; - &lt;tileWidth&gt;x&lt;tileHeight&gt;` 目录）。
/// </summary>
public sealed class SidecarTileSet
{
    /// <summary>
    /// Gets the sidecar root folder (e.g. /media/movies/电影A.trickplay).
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Gets the tile folder (e.g. /media/movies/电影A.trickplay/320 - 10x10).
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Gets the width of a single thumbnail.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Gets the height of a single thumbnail.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Gets the amount of thumbnails per row.
    /// </summary>
    public required int TileWidth { get; init; }

    /// <summary>
    /// Gets the amount of thumbnails per column.
    /// </summary>
    public required int TileHeight { get; init; }

    /// <summary>
    /// Gets the total amount of thumbnails.
    /// </summary>
    public required int ThumbnailCount { get; init; }

    /// <summary>
    /// Gets the interval in milliseconds between two thumbnails.
    /// </summary>
    public required int Interval { get; init; }

    /// <summary>
    /// Gets the peak bandwidth in bits per second.
    /// </summary>
    public required int Bandwidth { get; init; }

    /// <summary>
    /// Gets the ordered tile image files.
    /// </summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>
    /// 转成 Jellyfin 的 TrickplayInfo（只读用途，插件不写库）。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>TrickplayInfo.</returns>
    public TrickplayInfo ToTrickplayInfo(Guid itemId)
    {
        return new TrickplayInfo
        {
            ItemId = itemId,
            Width = Width,
            Height = Height,
            TileWidth = TileWidth,
            TileHeight = TileHeight,
            ThumbnailCount = ThumbnailCount,
            Interval = Interval,
            Bandwidth = Bandwidth
        };
    }

    /// <summary>
    /// 返回第 index 张瓦片图路径（越界返回 null）。
    /// </summary>
    /// <param name="index">Tile index.</param>
    /// <returns>Absolute path or null.</returns>
    public string? GetTilePath(int index)
    {
        if (index < 0 || index >= Files.Count)
        {
            return null;
        }

        return Files[index];
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} - {1}x{2} ({3} tiles, {4} thumbs, {5}ms) @ {6}",
            Width,
            TileWidth,
            TileHeight,
            Files.Count,
            ThumbnailCount,
            Interval,
            Directory);
    }
}
