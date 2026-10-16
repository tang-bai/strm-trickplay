using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Probe;
using Jellyfin.Plugin.StrmTrickplay.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StrmTrickplay.Api;

/// <summary>
/// 诊断接口：方便在不看日志的情况下确认插件是否生效。
/// 需要管理员权限。
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/StrmTrickplay")]
public sealed class StrmTrickplayController : ControllerBase
{
    /// <summary>
    /// 一张 160x90 的样例 JPEG（自检用）。
    /// </summary>
    private const string SampleJpegBase64 = "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCABaAKADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==";

    private readonly ILibraryManager _libraryManager;
    private readonly ISidecarTrickplayIndex _index;
    private readonly SidecarTrickplayParser _parser;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IStrmProbeService _probeService;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmTrickplayController"/> class.
    /// </summary>
    public StrmTrickplayController(
        ILibraryManager libraryManager,
        ISidecarTrickplayIndex index,
        SidecarTrickplayParser parser,
        IApplicationPaths applicationPaths,
        IStrmProbeService probeService,
        ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _index = index;
        _parser = parser;
        _applicationPaths = applicationPaths;
        _probeService = probeService;
        _taskManager = taskManager;
    }

    /// <summary>
    /// 插件与索引状态。
    /// </summary>
    /// <returns>Status payload.</returns>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var config = Plugin.CurrentConfiguration;

        return Ok(new
        {
            plugin = "STRM Trickplay",
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            enabled = config.Enabled,
            sidecarFolderNames = config.SidecarFolderNames,
            allowAnySiblingTrickplayFolder = config.AllowAnySiblingTrickplayFolder,
            defaultWidth = config.DefaultWidth,
            defaultTileWidth = config.DefaultTileWidth,
            defaultTileHeight = config.DefaultTileHeight,
            intervalOverrideMs = config.IntervalOverrideMs,
            cacheSeconds = config.CacheSeconds,
            emptyCacheSeconds = config.EmptyCacheSeconds,
            unknownRuntimeCacheSeconds = config.UnknownRuntimeCacheSeconds,
            verboseLogging = config.VerboseLogging,
            cachedItems = _index.CachedItemCount,
            trickplayManagerType = typeof(Trickplay.StrmTrickplayManager).FullName,
            mediaProbe = new
            {
                enabled = config.EnableMediaProbe,
                probeOnItemAdded = config.EnableProbeOnItemAdded,
                debounceSeconds = config.ProbeDebounceSeconds,
                parallelism = config.ProbeParallelism,
                timeoutSeconds = config.ProbeTimeoutSeconds,
                cooldownMs = config.ProbeCooldownMs,
                libraries = config.ProbeLibraryNames,
                queuedItems = _probeService.QueuedCount,
                lastRun = _probeService.LastRun is null ? null : new
                {
                    trigger = _probeService.LastRun.Trigger,
                    startedAt = _probeService.LastRun.StartedAt,
                    finishedAt = _probeService.LastRun.FinishedAt,
                    total = _probeService.LastRun.Total,
                    probed = _probeService.LastRun.Probed,
                    failed = _probeService.LastRun.Failed
                }
            }
        });
    }

    /// <summary>
    /// 查看某个条目的侧车解析详情（绕过缓存，直接读盘）。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Diagnostic payload.</returns>
    [HttpGet("Item/{itemId}")]
    public ActionResult GetItem([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "item not found", itemId = itemId.ToString("N", CultureInfo.InvariantCulture) });
        }

        if (item is not Video video)
        {
            return Ok(new { itemId = itemId.ToString("N", CultureInfo.InvariantCulture), type = item.GetType().Name, isVideo = false });
        }

        var config = Plugin.CurrentConfiguration;

        var roots = _parser.GetCandidateRoots(video, config)
            .Select(root => new
            {
                path = root,
                exists = SafeExists(root)
            })
            .ToList();

        var parsed = _parser.Parse(video, config)
            .Select(set => new
            {
                set.Directory,
                set.Width,
                set.Height,
                set.TileWidth,
                set.TileHeight,
                set.ThumbnailCount,
                set.Interval,
                set.Bandwidth,
                tileFileCount = set.Files.Count,
                firstTile = set.Files.Count > 0 ? set.Files[0] : null
            })
            .ToList();

        var cached = _index.GetResolutions(video)
            .ToDictionary(
                pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                pair => new
                {
                    pair.Value.Width,
                    pair.Value.Height,
                    pair.Value.TileWidth,
                    pair.Value.TileHeight,
                    pair.Value.ThumbnailCount,
                    pair.Value.Interval,
                    pair.Value.Bandwidth
                });

        var sources = video.GetMediaSources(false)
            .Select(source => new
            {
                source.Id,
                source.IsRemote,
                protocol = source.Protocol.ToString(),
                source.Path,
                runTimeTicks = source.RunTimeTicks
            })
            .ToList();

        return Ok(new
        {
            itemId = itemId.ToString("N", CultureInfo.InvariantCulture),
            name = video.Name,
            path = video.Path,
            containingFolder = video.ContainingFolderPath,
            isShortcut = video.IsShortcut,
            runTimeTicks = video.RunTimeTicks,
            candidateRoots = roots,
            parsedTileSets = parsed,
            cachedResolutions = cached,
            mediaSources = sources
        });
    }

    /// <summary>
    /// 失效并立即重新探测某个条目。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Diagnostic payload.</returns>
    [HttpPost("Item/{itemId}/Refresh")]
    public ActionResult RefreshItem([FromRoute] Guid itemId)
    {
        _index.Invalidate(itemId);

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return Ok(new { invalidated = true, isVideo = false });
        }

        var parsed = _parser.Parse(video, Plugin.CurrentConfiguration);

        return Ok(new
        {
            invalidated = true,
            isShortcut = video.IsShortcut,
            parsedTileSets = parsed.Select(set => new
            {
                set.Directory,
                set.Width,
                set.TileWidth,
                set.TileHeight,
                set.ThumbnailCount,
                set.Interval,
                tileFileCount = set.Files.Count
            }).ToList()
        });
    }

    /// <summary>
    /// 自检：在临时目录里造一份 <文件名>.trickplay/320 - 10x10/0..2.jpg 结构，直接跑解析器。
    /// 不需要任何真实媒体即可验证目录规范解析是否正确。
    /// </summary>
    /// <returns>Self test result.</returns>
    [HttpPost("SelfTest")]
    public ActionResult SelfTest()
    {
        var config = Plugin.CurrentConfiguration;
        var root = Path.Combine(_applicationPaths.TempDirectory, "strm-trickplay-selftest");

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            var tileDir = Path.Combine(root, "Sample.trickplay", "320 - 10x10");
            Directory.CreateDirectory(tileDir);

            var jpeg = Convert.FromBase64String(SampleJpegBase64);
            for (var i = 0; i < 3; i++)
            {
                System.IO.File.WriteAllBytes(Path.Combine(tileDir, i.ToString(CultureInfo.InvariantCulture) + ".jpg"), jpeg);
            }

            var strmPath = Path.Combine(root, "Sample.strm");
            System.IO.File.WriteAllText(strmPath, "http://example.com/sample.mkv");

            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = strmPath,
                RunTimeTicks = TimeSpan.FromMinutes(10).Ticks,
                IsShortcut = true
            };

            var sets = _parser.Parse(movie, config);

            return Ok(new
            {
                testRoot = root,
                strmFile = strmPath,
                tileFolder = tileDir,
                parsedCount = sets.Count,
                parsedTileSets = sets.Select(set => new
                {
                    set.Directory,
                    set.Width,
                    set.Height,
                    set.TileWidth,
                    set.TileHeight,
                    set.ThumbnailCount,
                    set.Interval,
                    set.Bandwidth,
                    tileFileCount = set.Files.Count,
                    firstTile = set.Files.Count > 0 ? set.Files[0] : null
                }).ToList(),
                expectations = new
                {
                    width = 320,
                    tileGrid = "10x10",
                    tileFileCount = 3,
                    height = 9,
                    interval = 2000,
                    thumbnailCount = 300,
                    note = "height=9 表示成功用 IImageEncoder 读到了 160x90 的瓦片图；若为 180 说明图像读取失败走了 16:9 兜底。"
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message, testRoot = root });
        }
    }

    /// <summary>
    /// 探测状态：待探测数量（可选统计，较慢）、队列长度、最近一次运行结果。
    /// </summary>
    /// <param name="count">是否顺便统计全库待探测条目数（默认 false，大库较慢）。</param>
    /// <returns>Probe status payload.</returns>
    [HttpGet("Probe/Status")]
    public ActionResult GetProbeStatus([FromQuery] bool count = false)
    {
        var config = Plugin.CurrentConfiguration;

        return Ok(new
        {
            enabled = config.EnableMediaProbe,
            probeOnItemAdded = config.EnableProbeOnItemAdded,
            debounceSeconds = config.ProbeDebounceSeconds,
            parallelism = config.ProbeParallelism,
            timeoutSeconds = config.ProbeTimeoutSeconds,
            cooldownMs = config.ProbeCooldownMs,
            libraries = config.ProbeLibraryNames,
            queuedItems = _probeService.QueuedCount,
            lastRun = _probeService.LastRun is null ? null : new
            {
                trigger = _probeService.LastRun.Trigger,
                startedAt = _probeService.LastRun.StartedAt,
                finishedAt = _probeService.LastRun.FinishedAt,
                total = _probeService.LastRun.Total,
                probed = _probeService.LastRun.Probed,
                failed = _probeService.LastRun.Failed
            },
            unprobedItems = count ? _probeService.GetUnprobedItems().Count : (int?)null
        });
    }

    /// <summary>
    /// 立即探测单个条目，并返回探测前后的时长 / 媒体流数量。
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Probe result.</returns>
    [HttpPost("Probe/Item/{itemId}")]
    public async Task<ActionResult> ProbeItem([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "item not found" });
        }

        var result = await _probeService.ProbeItemAsync(item, cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            itemId = itemId.ToString("N", CultureInfo.InvariantCulture),
            name = item.Name,
            needsProbe = _probeService.NeedsProbe(item),
            success = result.Success,
            reason = result.Reason,
            runTimeBefore = result.RunTimeBefore,
            runTimeAfter = result.RunTimeAfter,
            runTimeSeconds = result.RunTimeAfter is null ? null : (double?)(result.RunTimeAfter / (double)TimeSpan.TicksPerSecond),
            streamsBefore = result.StreamsBefore,
            streamsAfter = result.StreamsAfter
        });
    }

    /// <summary>
    /// 把「探测 STRM 媒体信息」计划任务排入队列（立即后台执行）。
    /// </summary>
    /// <returns>Queued flag.</returns>
    [HttpPost("Probe/Run")]
    public ActionResult QueueProbeTask()
    {
        _taskManager.QueueIfNotRunning<ProbeStrmMediaInfoTask>();
        return Ok(new { queued = true, task = "探测 STRM 媒体信息" });
    }

    /// <summary>
    /// 把「刷新 STRM Trickplay 索引」计划任务排入队列。
    /// </summary>
    /// <returns>Queued flag.</returns>
    [HttpPost("Index/Rebuild")]
    public ActionResult QueueIndexRebuild()
    {
        _index.Clear();
        _taskManager.QueueIfNotRunning<RefreshStrmTrickplayIndexTask>();
        return Ok(new { queued = true, task = "刷新 STRM Trickplay 索引" });
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
