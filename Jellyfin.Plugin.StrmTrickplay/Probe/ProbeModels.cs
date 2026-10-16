using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StrmTrickplay.Probe;

/// <summary>
/// 单个条目的探测结果。
/// </summary>
/// <param name="Success">是否成功取到媒体信息。</param>
/// <param name="Reason">结果说明（ok / timeout / no-data / 异常信息）。</param>
/// <param name="RunTimeBefore">探测前时长。</param>
/// <param name="RunTimeAfter">探测后时长。</param>
/// <param name="StreamsBefore">探测前媒体流数量。</param>
/// <param name="StreamsAfter">探测后媒体流数量。</param>
public sealed record ProbeItemResult(
    bool Success,
    string Reason,
    long? RunTimeBefore,
    long? RunTimeAfter,
    int StreamsBefore,
    int StreamsAfter);

/// <summary>
/// 批量探测结果。
/// </summary>
/// <param name="Total">总数。</param>
/// <param name="Probed">成功数。</param>
/// <param name="Failed">失败数。</param>
/// <param name="Duration">耗时。</param>
/// <param name="FailedNames">失败条目名称（最多保留前若干条）。</param>
public sealed record ProbeBatchResult(
    int Total,
    int Probed,
    int Failed,
    TimeSpan Duration,
    IReadOnlyList<string> FailedNames);

/// <summary>
/// 最近一次运行的统计。
/// </summary>
/// <param name="Trigger">触发方式（计划任务 / 新增条目 / 手动）。</param>
/// <param name="StartedAt">开始时间。</param>
/// <param name="FinishedAt">结束时间。</param>
/// <param name="Total">总数。</param>
/// <param name="Probed">成功数。</param>
/// <param name="Failed">失败数。</param>
public sealed record ProbeRunStats(
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Total,
    int Probed,
    int Failed);
