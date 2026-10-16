using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Probe;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Tasks;

/// <summary>
/// 计划任务：为缺少媒体信息的 .strm 条目跑一次远端探测（ffprobe）。
/// 补齐时长 / 编码 / 分辨率 / 音轨，并顺带让 trickplay 的缩略图间隔可以精确推断。
/// </summary>
public sealed class ProbeStrmMediaInfoTask : IScheduledTask
{
    private readonly IStrmProbeService _probeService;
    private readonly ILogger<ProbeStrmMediaInfoTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeStrmMediaInfoTask"/> class.
    /// </summary>
    public ProbeStrmMediaInfoTask(IStrmProbeService probeService, ILogger<ProbeStrmMediaInfoTask> logger)
    {
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "探测 STRM 媒体信息";

    /// <inheritdoc />
    public string Key => "StrmTrickplayProbeMediaInfo";

    /// <inheritdoc />
    public string Description => "为缺少媒体信息的 .strm 条目执行远端探测，补齐时长 / 编码 / 分辨率 / 音轨（Jellyfin 默认跳过 .strm 的库扫描探测）。";

    /// <inheritdoc />
    public string Category => "STRM Trickplay";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.CurrentConfiguration.EnableMediaProbe)
        {
            _logger.LogInformation("[StrmTrickplay] 媒体信息探测已在插件设置中关闭，跳过本次任务");
            progress?.Report(100);
            return;
        }

        var result = await _probeService.ProbeAllUnprobedAsync(progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[StrmTrickplay] 计划任务结束：共 {Total}，成功 {Probed}，失败 {Failed}",
            result.Total,
            result.Probed,
            result.Failed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}
