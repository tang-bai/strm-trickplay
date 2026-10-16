using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Playback;

/// <summary>
/// IMediaSourceManager 装饰器：唯一的改动是——播放路径上，对"媒体信息已经完整"的 .strm 条目
/// 把 <c>allowMediaProbe</c> 置为 false，从而跳过核心每次点播都强制触发的那次远端 ffprobe（起播加速）。
/// 其余 20 个成员全部原样透传，未探测过的条目行为完全不变。
/// </summary>
public sealed class StrmMediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<StrmMediaSourceManagerDecorator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmMediaSourceManagerDecorator"/> class.
    /// </summary>
    /// <param name="inner">核心实现。</param>
    /// <param name="logger">Logger.</param>
    public StrmMediaSourceManagerDecorator(IMediaSourceManager inner, ILogger<StrmMediaSourceManagerDecorator> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        var effectiveAllowProbe = allowMediaProbe;

        if (allowMediaProbe && Plugin.CurrentConfiguration.EnableFastStartup)
        {
            try
            {
                var streams = _inner.GetMediaStreams(item.Id);
                if (PlaybackProbePolicy.ShouldSkipProbe(item, streams, true))
                {
                    effectiveAllowProbe = false;
                    _logger.LogInformation(
                        "[StrmTrickplay] 起播加速：{Name} 的媒体信息已完整，跳过本次远端探测",
                        item.Name ?? item.Path);
                }
            }
            catch (Exception ex)
            {
                // 判定失败时保持核心行为，绝不因为加速而影响播放
                _logger.LogDebug(ex, "[StrmTrickplay] 起播探测判定失败，按核心原行为处理");
            }
        }

        return _inner.GetPlaybackMediaSources(item, user, effectiveAllowProbe, enablePathSubstitution, cancellationToken);
    }

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
    {
        return _inner.GetMediaAttachments(itemId);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
    {
        return _inner.GetMediaAttachments(query);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        return _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
    {
        return _inner.OpenLiveStream(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
    {
        return _inner.OpenLiveStreamInternal(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
    {
        return _inner.GetLiveStream(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
    {
        return _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
    }

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfo(string id)
    {
        return _inner.GetLiveStreamInfo(id);
    }

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
    {
        return _inner.GetLiveStreamInfoByUniqueId(uniqueId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
    {
        return _inner.GetRecordingStreamMediaSources(info, cancellationToken);
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id)
    {
        return _inner.CloseLiveStream(id);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
    {
        return _inner.GetLiveStreamMediaInfo(id, cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsDirectStream(string path, MediaProtocol protocol)
    {
        return _inner.SupportsDirectStream(path, protocol);
    }

    /// <inheritdoc />
    public MediaProtocol GetPathProtocol(string path)
    {
        return _inner.GetPathProtocol(path);
    }

    /// <inheritdoc />
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
    {
        _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    }

    /// <inheritdoc />
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
    {
        return _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    }
}
