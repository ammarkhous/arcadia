using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.ItTools.SystemInfo;
using Microsoft.Extensions.DependencyInjection;

namespace Arcadia.ItTools;

public static class ItToolsServiceCollectionExtensions
{
    /// <summary>Registers the Windows implementations of the IT Tools services. All of them work locally.</summary>
    public static IServiceCollection AddArcadiaItTools(this IServiceCollection services)
    {
        services.AddSingleton<ISystemInformationService, SystemInformationService>();
        services.AddSingleton<IPerformanceMonitor, PerformanceMonitor>();
        services.AddSingleton<INetworkInformationService, NetworkInformationService>();
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<ITracerouteService, TracerouteService>();
        services.AddSingleton<IDnsLookupService, DnsLookupService>();
        services.AddSingleton<IPortTestService, PortTestService>();
        services.AddSingleton<ILocalNetworkScanner, LocalNetworkScanner>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
        services.AddSingleton<IEventLogService, EventLogService>();
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<ILargeFileFinder, LargeFileFinder>();
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IQuickCommandLauncher, QuickCommandLauncher>();
        return services;
    }
}
