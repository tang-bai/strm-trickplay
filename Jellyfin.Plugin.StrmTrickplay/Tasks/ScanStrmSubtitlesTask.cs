using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Tasks;

/// <summary>
/// 计划任务：为缺少外挂字幕的 .strm 条目扫描同级目录（含 Subs 等子目录）并挂载字幕（支持 ASS）。
/// 核心的 `MediaInfoResolver` 对非 file 协议的条目直接跳过外挂字幕扫描，所以需要插件补这一步。
/// </summary>
public sealed class ScanStrmSubtitlesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly IStrmSubtitleService _subtitleService;
    private readonly ILogger<ScanStrmSubtitlesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanStrmSubtitlesTask"/> class.
    /// </summary>
    public ScanStrmSubtitlesTask(
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreamRepository,
        IStrmSubtitleService subtitleService,
        ILogger<ScanStrmSubtitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaStreamRepository = mediaStreamRepository;
        _subtitleService = subtitleService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "扫描 STRM 外挂字幕";

    /// <inheritdoc />
    public string Key => "StrmTrickplayScanSubtitles";

    /// <inheritdoc />
    public string Description => "为 .strm 条目扫描同级目录（含 Subs / 字幕 等子目录）的外挂字幕并挂载，支持 ASS / SSA / SRT / VTT 等格式。Jellyfin 核心对非 file 协议的 strm 不会做这一步。";

    /// <inheritdoc />
    public string Category => "STRM Trickplay";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.CurrentConfiguration.EnableSidecarSubtitles)
        {
            _logger.LogInformation("[StrmTrickplay] 外挂字幕已关闭，跳过本任务");
            progress?.Report(100);
            return;
        }

        IReadOnlyList<Guid> ids;
        try
        {
            ids = _libraryManager.GetItemIds(new InternalItemsQuery { Recursive = true, IsFolder = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmTrickplay] 枚举媒体库失败");
            progress?.Report(100);
            return;
        }

        var candidates = new List<BaseItem>();
        foreach (var id in ids)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null || !_subtitleService.IsHandled(item))
            {
                continue;
            }

            var existing = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery
            {
                ItemId = item.Id,
                Type = MediaStreamType.Subtitle
            }) ?? [];

            if (!existing.Any(s => s.IsExternal))
            {
                candidates.Add(item);
            }
        }

        _logger.LogInformation("[StrmTrickplay] 待补外挂字幕的 .strm 条目: {Count}", candidates.Count);
        progress?.Report(10);

        var (items, added) = await _subtitleService.AttachManyAsync(candidates, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("[StrmTrickplay] 外挂字幕扫描完成: 处理 {Items} 个条目，挂载 {Added} 条字幕", items, added);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
            }
        ];
    }
}
