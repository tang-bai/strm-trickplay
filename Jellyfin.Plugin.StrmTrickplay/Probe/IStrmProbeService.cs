using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Probe;

/// <summary>
/// STRM 媒体信息探测：调用 Jellyfin 的 ffprobe 流程，为 .strm 条目补齐时长 / 编码 / 分辨率 / 音轨。
/// 顺带的好处：strm 有了时长之后，trickplay 的缩略图间隔可以按官方算法精确推断。
/// </summary>
public interface IStrmProbeService
{
    /// <summary>
    /// Gets the number of items waiting in the catch-up queue.
    /// </summary>
    int QueuedCount { get; }

    /// <summary>
    /// Gets the statistics of the last probe run.
    /// </summary>
    ProbeRunStats? LastRun { get; }

    /// <summary>
    /// 判断某个条目是否需要探测（是 .strm 且缺少媒体流或时长）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True when probing is needed.</returns>
    bool NeedsProbe(BaseItem item);

    /// <summary>
    /// 找出所有需要探测的条目（按配置里的媒体库过滤）。
    /// </summary>
    /// <returns>Items that need probing.</returns>
    IReadOnlyList<BaseItem> GetUnprobedItems();

    /// <summary>
    /// 把新加入的条目放进防抖队列（随库扫描自动探测）。
    /// </summary>
    /// <param name="item">The item.</param>
    void EnqueueForCatchUp(BaseItem item);

    /// <summary>
    /// 清空并探测防抖队列里的条目。
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batch result.</returns>
    Task<ProbeBatchResult> DrainCatchUpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 探测全部尚未探测的条目。
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batch result.</returns>
    Task<ProbeBatchResult> ProbeAllUnprobedAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// 探测单个条目。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Item result.</returns>
    Task<ProbeItemResult> ProbeItemAsync(BaseItem item, CancellationToken cancellationToken);
}
