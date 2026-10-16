using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.StrmTrickplay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StrmTrickplay;

/// <summary>
/// 主插件类：让 .strm 条目复用同级目录下已经生成好的 &lt;名字&gt;.trickplay 瓦片。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// 插件 GUID（与 meta.json 保持一致，不要修改）。
    /// </summary>
    public const string PluginGuidString = "3f9a7c14-5b62-4d8e-9a10-7c6e2f5b8d31";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "STRM Trickplay";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuidString);

    /// <inheritdoc />
    public override string Description => "让 Jellyfin 中的 .strm 条目复用媒体目录下已生成的 .trickplay 瓦片目录，在播放时显示进度条缩略图预览。";

    /// <summary>
    /// Gets the current configuration (null-safe).
    /// </summary>
    public static PluginConfiguration CurrentConfiguration => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "StrmTrickplay",
                DisplayName = Name,

                // 在控制台左侧菜单的「插件」分类里出现一个入口，点进去就是本插件设置页
                EnableInMainMenu = true,
                MenuIcon = "movie",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
