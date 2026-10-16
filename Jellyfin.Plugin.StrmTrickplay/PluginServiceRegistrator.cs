using System;
using System.Linq;
using Jellyfin.Plugin.StrmTrickplay.EventHandlers;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay;

/// <summary>
/// 插件 DI 注册。插件服务在核心服务之后注册（ApplicationHost.RegisterServices -&gt; PluginManager.RegisterServices），
/// 因此这里最后注册的 ITrickplayManager 会覆盖核心实现，被 TrickplayController / DtoService / DynamicHlsHelper 使用。
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SidecarTrickplayParser>();
        serviceCollection.AddSingleton<ISidecarTrickplayIndex, SidecarTrickplayIndex>();
        serviceCollection.AddSingleton<ITrickplayGenerator, NoopTrickplayGenerator>();
        serviceCollection.AddHostedService<LibraryChangeListener>();

        // 保留"我们之前最后一次注册"的实现作为回退，支持与其它插件的装饰链共存
        var previous = serviceCollection.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ITrickplayManager));

        serviceCollection.AddSingleton<ITrickplayManager>(serviceProvider =>
        {
            var fallback = ResolvePrevious(serviceProvider, previous)
                ?? throw new InvalidOperationException(
                    "未找到核心 ITrickplayManager 注册，STRM Trickplay 装饰器无法安装。");

            return new StrmTrickplayManager(
                fallback,
                serviceProvider.GetRequiredService<ISidecarTrickplayIndex>(),
                serviceProvider.GetRequiredService<ITrickplayGenerator>(),
                serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<StrmTrickplayManager>());
        });
    }

    private static ITrickplayManager? ResolvePrevious(IServiceProvider serviceProvider, ServiceDescriptor? descriptor)
    {
        return descriptor switch
        {
            null => null,
            { ImplementationInstance: ITrickplayManager instance } => instance,
            { ImplementationFactory: { } factory } => factory(serviceProvider) as ITrickplayManager,
            { ImplementationType: { } type } => ActivatorUtilities.CreateInstance(serviceProvider, type) as ITrickplayManager,
            _ => null
        };
    }
}
