using System.Reflection;
using System.Runtime.Loader;
using Moonfin.Server.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Moonfin.Server.Services;

/// <summary>
/// Startup task that registers Moonfin's file transformations with the File Transformation
/// plugin, enabling automatic injection of frontend assets into the Jellyfin web interface.
/// </summary>
public class FileTransformationStartupService : IScheduledTask
{
    public string Name => "Moonfin Startup";
    public string Key => "Moonfin.Server.Startup";
    public string Description => "Registers Moonfin's file transformations to inject frontend assets into Jellyfin web UI.";
    public string Category => "Startup Services";

    private readonly ILogger<FileTransformationStartupService> _logger;

    public FileTransformationStartupService(ILogger<FileTransformationStartupService> logger)
    {
        _logger = logger;
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Moonfin Startup: Registering file transformations.");

        var payload = new JObject
        {
            { "id", "8c5d0e91-0001-4b6d-9e3f-1a7c8d9e0f2b" },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", GetType().Assembly.FullName },
            { "callbackClass", typeof(TransformationPatches).FullName },
            { "callbackMethod", nameof(TransformationPatches.IndexHtml) }
        };

        Assembly? ftAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);

        if (ftAssembly == null)
        {
            _logger.LogWarning(
                "Moonfin: File Transformation plugin not found. Frontend auto-injection is disabled. " +
                "Install from https://github.com/IAmParadox27/jellyfin-plugin-file-transformation");
            return Task.CompletedTask;
        }

        Type? pluginInterfaceType = ftAssembly
            .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

        if (pluginInterfaceType == null)
        {
            _logger.LogWarning(
                "Moonfin: File Transformation PluginInterface type not available. " +
                "Ensure File Transformation plugin is v2.2.1.0 or later.");
            return Task.CompletedTask;
        }

        pluginInterfaceType.GetMethod("RegisterTransformation")
            ?.Invoke(null, new object?[] { payload });

        _logger.LogInformation("Moonfin: Registered index.html transformation.");

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerStartup
        };
    }
}
