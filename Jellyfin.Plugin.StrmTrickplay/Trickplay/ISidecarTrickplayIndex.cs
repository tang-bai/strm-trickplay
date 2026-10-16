using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 侧车 trickplay 瓦片索引（懒探测 + 缓存 + 事件失效）。
/// </summary>
public interface ISidecarTrickplayIndex
{
    /// <summary>
    /// Gets the amount of cached items (including negative cache).
    /// </summary>
    int CachedItemCount { get; }

    /// <summary>
    /// 返回该条目可用的所有分辨率信息（width -&gt; info）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Resolution map (empty when unavailable).</returns>
    IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(BaseItem item);

    /// <summary>
    /// 返回该 item id 可用的所有分辨率信息（用于 HLS 播放列表路径）。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Resolution map (empty when unavailable).</returns>
    IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(Guid itemId);

    /// <summary>
    /// 返回指定宽度下第 index 张瓦片图的绝对路径。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="width">Thumbnail width.</param>
    /// <param name="index">Tile index.</param>
    /// <returns>Absolute path or null.</returns>
    string? GetTilePath(BaseItem item, int width, int index);

    /// <summary>
    /// 返回指定宽度对应的瓦片目录。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="width">Thumbnail width.</param>
    /// <returns>Absolute directory or null.</returns>
    string? GetTileDirectory(BaseItem item, int width);

    /// <summary>
    /// 返回该条目的全部瓦片集（调试用，绕过缓存）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Tile sets.</returns>
    IReadOnlyList<SidecarTileSet> GetTileSets(BaseItem item);

    /// <summary>
    /// 失效某个条目的缓存。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    void Invalidate(Guid itemId);

    /// <summary>
    /// 清空全部缓存。
    /// </summary>
    void Clear();

    /// <summary>
    /// 预扫某个条目（计划任务用）。
    /// </summary>
    /// <param name="video">The video.</param>
    void WarmUp(Video video);
}
