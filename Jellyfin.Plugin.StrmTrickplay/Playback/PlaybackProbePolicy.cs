using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Playback;

/// <summary>
/// 起播阶段的探测策略。
///
/// Jellyfin 核心在 <c>MediaSourceManager.GetPlaybackMediaSources</c> 里对**所有** .strm 条目无条件执行一次
/// `RefreshMetadata(EnableRemoteContentProbe = true, FullRefresh)`（判断依据是路径后缀，而不是"是否缺少媒体信息"），
/// 而 <c>FFProbeVideoInfo.ProbeVideo</c> 在 `EnableRemoteContentProbe` 为真时会再次对远端直链跑 ffprobe ——
/// 这就是 .strm 点播起播慢的主因，且"预先提取过媒体信息"并不能消除它。
///
/// 这里给出安全跳过条件：只有当条目确实是 .strm、已有对应类型的媒体流、并且已知时长时才跳过；
/// 任何一条不满足就保持原样（继续探测），从而不会影响未探测条目的正常播放。
/// </summary>
public static class PlaybackProbePolicy
{
    /// <summary>
    /// 判断是否可以在起播阶段跳过核心强制的远端探测。
    /// </summary>
    /// <param name="item">正在播放的条目。</param>
    /// <param name="streams">该条目已存储的媒体流。</param>
    /// <param name="enabled">功能开关。</param>
    /// <returns>True 表示可以跳过。</returns>
    public static bool ShouldSkipProbe(BaseItem? item, IReadOnlyList<MediaStream>? streams, bool enabled)
    {
        if (!enabled || item is null || streams is null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(item.Path)
            || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 时长是"信息完整"的关键标志；未知时长说明还没探测成功
        if (item.RunTimeTicks is null or <= 0)
        {
            return false;
        }

        return item.MediaType switch
        {
            MediaType.Video => streams.Any(stream => stream.Type == MediaStreamType.Video),
            MediaType.Audio => streams.Any(stream => stream.Type == MediaStreamType.Audio),
            _ => false
        };
    }
}
