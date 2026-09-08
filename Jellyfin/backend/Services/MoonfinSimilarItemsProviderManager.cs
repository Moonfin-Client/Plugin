using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Registers "Moonfin Recommends" with Jellyfin 12's similar items pipeline when the host exposes
/// one. The provider is emitted at runtime rather than declared because the plugin compiles against
/// Jellyfin.Controller 10.10, where none of those interfaces exist yet.
/// </summary>
public class MoonfinSimilarItemsProviderManager : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MoonfinSimilarItemsService _similarItemsService;
    private readonly ILogger<MoonfinSimilarItemsProviderManager> _logger;

    private readonly List<object> _stockProviders = [];

    private Type? _localProviderType;
    private MethodInfo? _stockSupports;
    private MethodInfo? _stockGetSimilarItemsAsync;
    private PropertyInfo? _queryUser;
    private PropertyInfo? _queryLimit;
    private PropertyInfo? _queryExcludeItemIds;

    public MoonfinSimilarItemsProviderManager(
        IServiceProvider serviceProvider,
        MoonfinSimilarItemsService similarItemsService,
        ILogger<MoonfinSimilarItemsProviderManager> logger)
    {
        _serviceProvider = serviceProvider;
        _similarItemsService = similarItemsService;
        _logger = logger;
    }

    private static bool IsProviderEnabled =>
        MoonfinPlugin.Instance?.Configuration?.RecommendationsProviderEnabled ?? true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            RegisterSimilarItemsProvider();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Moonfin similar items provider with Jellyfin host.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void RegisterSimilarItemsProvider()
    {
        var simMgrInterfaceType = Type.GetType("MediaBrowser.Controller.Library.ISimilarItemsManager, MediaBrowser.Controller");
        if (simMgrInterfaceType == null)
        {
            _logger.LogDebug("ISimilarItemsManager not found, so this is Jellyfin 10.x or Emby. Provider registration skipped.");
            return;
        }

        var provBaseType = Type.GetType("MediaBrowser.Controller.Library.ISimilarItemsProvider, MediaBrowser.Controller");
        var localProvType = Type.GetType("MediaBrowser.Controller.Library.ILocalSimilarItemsProvider, MediaBrowser.Controller");

        if (localProvType == null || provBaseType == null)
        {
            _logger.LogWarning("Required similarity provider types missing from MediaBrowser.Controller.");
            return;
        }

        var simMgr = _serviceProvider.GetService(simMgrInterfaceType);
        if (simMgr == null)
        {
            _logger.LogWarning("ISimilarItemsManager resolved as null from IServiceProvider.");
            return;
        }

        var addPartsMethod = simMgrInterfaceType.GetMethod("AddParts");
        if (addPartsMethod == null)
        {
            _logger.LogWarning("ISimilarItemsManager.AddParts method not found.");
            return;
        }

        // AddParts replaces the provider set rather than appending to it, so the providers Jellyfin
        // registered at startup have to be read back and passed in again. If that read ever fails
        // we have to leave the pipeline alone, because registering Moonfin on its own would
        // silently drop every stock provider.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var existingField = simMgr.GetType().GetField("_similarItemsProviders", flags);
        if (existingField?.GetValue(simMgr) is not Array existingArr)
        {
            _logger.LogWarning(
                "Could not read the existing similar items providers from {ManagerType}, so Moonfin Recommends was not registered. Registering it would have removed Jellyfin's own providers.",
                simMgr.GetType().FullName);
            return;
        }

        var providerInstance = CreateDynamicProvider(localProvType, provBaseType);
        if (providerInstance == null)
        {
            return;
        }

        var allList = new List<object>();
        foreach (var existing in existingArr)
        {
            if (existing != null && !existing.GetType().FullName!.Contains("Moonfin", StringComparison.Ordinal))
            {
                allList.Add(existing);
            }
        }

        lock (_stockProviders)
        {
            _stockProviders.Clear();
            _stockProviders.AddRange(allList);
        }

        // The manager sorts providers by their configured order and that sort is stable, so sitting
        // at the head of the array is what puts Moonfin first among equals.
        allList.Insert(0, providerInstance);

        var arr = Array.CreateInstance(provBaseType, allList.Count);
        for (var i = 0; i < allList.Count; i++)
        {
            arr.SetValue(allList[i], i);
        }

        addPartsMethod.Invoke(simMgr, [arr]);
        _logger.LogInformation(
            "Registered 'Moonfin Recommends' ahead of {ExistingCount} existing similar items providers.",
            allList.Count - 1);
    }

    private object? CreateDynamicProvider(Type localProvType, Type provBaseType)
    {
        var ifaceSupports = localProvType.GetMethod("Supports")!;
        var ifaceGetSimilar = localProvType.GetMethod("GetSimilarItemsAsync")!;
        var queryType = ifaceGetSimilar.GetParameters()[1].ParameterType;

        var ifacePropType = provBaseType.GetProperty("Type")!;
        var metaPluginEnumType = ifacePropType.PropertyType;
        if (!Enum.TryParse(metaPluginEnumType, "LocalSimilarityProvider", out var localSimilarityKind) || localSimilarityKind == null)
        {
            _logger.LogWarning("{EnumType} has no LocalSimilarityProvider member, so Moonfin Recommends was not registered.", metaPluginEnumType.FullName);
            return null;
        }

        var asmName = new AssemblyName("Moonfin.Server.DynamicSimilarity");
        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        var moduleBuilder = asmBuilder.DefineDynamicModule("MainModule");

        var typeBuilder = moduleBuilder.DefineType(
            "MoonfinSimilarItemsProviderDynamic",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(object),
            [localProvType]);

        const MethodAttributes ifaceMethodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.Final;
        const MethodAttributes propMethodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Final;

        var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var ilCtor = ctor.GetILGenerator();
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        ilCtor.Emit(OpCodes.Ret);

        var ifacePropName = provBaseType.GetProperty("Name")!;
        var propName = typeBuilder.DefineProperty("Name", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
        var getName = typeBuilder.DefineMethod("get_Name", propMethodAttrs, typeof(string), Type.EmptyTypes);
        var ilName = getName.GetILGenerator();
        ilName.Emit(OpCodes.Ldstr, "Moonfin Recommends");
        ilName.Emit(OpCodes.Ret);
        propName.SetGetMethod(getName);
        typeBuilder.DefineMethodOverride(getName, ifacePropName.GetGetMethod()!);

        var propType = typeBuilder.DefineProperty("Type", PropertyAttributes.None, metaPluginEnumType, Type.EmptyTypes);
        var getType = typeBuilder.DefineMethod("get_Type", propMethodAttrs, metaPluginEnumType, Type.EmptyTypes);
        var ilType = getType.GetILGenerator();
        ilType.Emit(OpCodes.Ldc_I4, Convert.ToInt32(localSimilarityKind, CultureInfo.InvariantCulture));
        ilType.Emit(OpCodes.Ret);
        propType.SetGetMethod(getType);
        typeBuilder.DefineMethodOverride(getType, ifacePropType.GetGetMethod()!);

        // Jellyfin only caches remote providers, so this value is never read. The interface still
        // asks for one.
        var ifacePropCache = provBaseType.GetProperty("CacheDuration")!;
        var nullableTimeSpanType = ifacePropCache.PropertyType;
        var propCache = typeBuilder.DefineProperty("CacheDuration", PropertyAttributes.None, nullableTimeSpanType, Type.EmptyTypes);
        var getCache = typeBuilder.DefineMethod("get_CacheDuration", propMethodAttrs, nullableTimeSpanType, Type.EmptyTypes);
        var ilCache = getCache.GetILGenerator();
        ilCache.Emit(OpCodes.Ldc_R8, 24.0);
        ilCache.Emit(OpCodes.Call, typeof(TimeSpan).GetMethod("FromHours", [typeof(double)])!);
        ilCache.Emit(OpCodes.Newobj, nullableTimeSpanType.GetConstructor([typeof(TimeSpan)])!);
        ilCache.Emit(OpCodes.Ret);
        propCache.SetGetMethod(getCache);
        typeBuilder.DefineMethodOverride(getCache, ifacePropCache.GetGetMethod()!);

        // The emitted methods hold no state of their own, they just forward to handlers on this
        // instance through static delegate fields.
        var fSupports = typeBuilder.DefineField("_supportsDelegate", typeof(Func<Type, bool>), FieldAttributes.Public | FieldAttributes.Static);
        var fGetSimilar = typeBuilder.DefineField("_getSimilarDelegate", typeof(Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>), FieldAttributes.Public | FieldAttributes.Static);

        var mSupports = typeBuilder.DefineMethod("Supports", ifaceMethodAttrs, typeof(bool), ifaceSupports.GetParameters().Select(p => p.ParameterType).ToArray());
        var ilSupports = mSupports.GetILGenerator();
        ilSupports.Emit(OpCodes.Ldsfld, fSupports);
        ilSupports.Emit(OpCodes.Ldarg_1);
        ilSupports.Emit(OpCodes.Callvirt, typeof(Func<Type, bool>).GetMethod("Invoke")!);
        ilSupports.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mSupports, ifaceSupports);

        var mGetSimilar = typeBuilder.DefineMethod(
            "GetSimilarItemsAsync",
            ifaceMethodAttrs,
            ifaceGetSimilar.ReturnType,
            ifaceGetSimilar.GetParameters().Select(p => p.ParameterType).ToArray());
        var ilGetSimilar = mGetSimilar.GetILGenerator();
        ilGetSimilar.Emit(OpCodes.Ldsfld, fGetSimilar);
        ilGetSimilar.Emit(OpCodes.Ldarg_1);
        ilGetSimilar.Emit(OpCodes.Ldarg_2);
        ilGetSimilar.Emit(OpCodes.Ldarg_3);
        ilGetSimilar.Emit(OpCodes.Callvirt, typeof(Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>).GetMethod("Invoke")!);
        ilGetSimilar.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mGetSimilar, ifaceGetSimilar);

        var generatedType = typeBuilder.CreateType();
        if (generatedType == null)
        {
            _logger.LogWarning("Failed to create dynamic Moonfin similar items provider.");
            return null;
        }

        generatedType.GetField("_supportsDelegate")!.SetValue(null, (Func<Type, bool>)HandleSupports);
        generatedType.GetField("_getSimilarDelegate")!.SetValue(
            null,
            (Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>)HandleGetSimilarAsync);

        _localProviderType = localProvType;
        _stockSupports = ifaceSupports;
        _stockGetSimilarItemsAsync = ifaceGetSimilar;
        _queryUser = queryType.GetProperty("User");
        _queryLimit = queryType.GetProperty("Limit");
        _queryExcludeItemIds = queryType.GetProperty("ExcludeItemIds");

        return Activator.CreateInstance(generatedType);
    }

    /// <summary>
    /// Jellyfin asks this on every similar items request, so an admin turning the setting off takes
    /// Moonfin back out of the pipeline straight away without needing a restart.
    /// </summary>
    private bool HandleSupports(Type? itemType)
    {
        if (itemType == null || !IsProviderEnabled)
        {
            return false;
        }

        return typeof(Movie).IsAssignableFrom(itemType) || typeof(Series).IsAssignableFrom(itemType);
    }

    private async Task<IReadOnlyList<BaseItem>> HandleGetSimilarAsync(
        BaseItem item,
        object? rawQuery,
        CancellationToken cancellationToken)
    {
        if (WantsStockEngine())
        {
            return await InvokeStockProviderAsync(item, rawQuery, cancellationToken).ConfigureAwait(false);
        }

        object? user = null;
        int? limit = null;
        IReadOnlyList<Guid>? excludeItemIds = null;

        if (rawQuery != null)
        {
            user = _queryUser?.GetValue(rawQuery);
            limit = _queryLimit?.GetValue(rawQuery) as int?;
            excludeItemIds = _queryExcludeItemIds?.GetValue(rawQuery) as IReadOnlyList<Guid>;
        }

        try
        {
            return await _similarItemsService.GetSimilarItemsAsync(
                item,
                user,
                limit,
                excludeItemIds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Returning nothing lets the stock providers fill the slots instead of failing the
            // whole request.
            _logger.LogError(ex, "Error generating similar items for '{ItemName}'.", item.Name);
            return Array.Empty<BaseItem>();
        }
    }

    /// <summary>
    /// Lets a caller ask for Jellyfin's own recommendations on a single request, so the two engines
    /// can be compared without touching the server config.
    /// </summary>
    private bool WantsStockEngine()
    {
        var httpContext = _serviceProvider.GetService(typeof(IHttpContextAccessor)) is IHttpContextAccessor accessor
            ? accessor.HttpContext
            : null;

        if (httpContext == null)
        {
            return false;
        }

        var query = httpContext.Request.Query;
        if (string.Equals(query["bypass"].ToString(), "moonfin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var engine = query["engine"].ToString();
        return string.Equals(engine, "jellyfin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "stock", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<BaseItem>> InvokeStockProviderAsync(
        BaseItem item,
        object? rawQuery,
        CancellationToken cancellationToken)
    {
        if (_localProviderType == null || _stockSupports == null || _stockGetSimilarItemsAsync == null)
        {
            return Array.Empty<BaseItem>();
        }

        List<object> providers;
        lock (_stockProviders)
        {
            providers = new List<object>(_stockProviders);
        }

        var itemType = item.GetType();

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerType = provider.GetType();
            if (!_localProviderType.IsAssignableFrom(providerType))
            {
                continue;
            }

            try
            {
                if (_stockSupports.Invoke(provider, [itemType]) is not true)
                {
                    continue;
                }

                if (_stockGetSimilarItemsAsync.Invoke(provider, [item, rawQuery, cancellationToken]) is Task<IReadOnlyList<BaseItem>> typedTask)
                {
                    return await typedTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invoke stock similarity provider {ProviderType} for '{ItemName}'.", providerType.FullName, item.Name);
            }
        }

        return Array.Empty<BaseItem>();
    }
}
