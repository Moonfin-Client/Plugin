using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Hosted service that detects whether Jellyfin 12's ISimilarItemsManager is available
/// and dynamically generates and registers an ILocalSimilarItemsProvider implementation ("Moonfin Recommends").
/// </summary>
public class MoonfinSimilarItemsProviderManager : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MoonfinSimilarItemsService _similarItemsService;
    private readonly ILogger<MoonfinSimilarItemsProviderManager> _logger;

    private static readonly List<object> _stockProviders = [];

    public MoonfinSimilarItemsProviderManager(
        IServiceProvider serviceProvider,
        MoonfinSimilarItemsService similarItemsService,
        ILogger<MoonfinSimilarItemsProviderManager> logger)
    {
        _serviceProvider = serviceProvider;
        _similarItemsService = similarItemsService;
        _logger = logger;
    }

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
            _logger.LogInformation("MediaBrowser.Controller.Library.ISimilarItemsManager not found; running on Jellyfin 10.x or Emby. Native similar items provider registration skipped.");
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

        var providerInstance = CreateDynamicProvider(localProvType, provBaseType);
        if (providerInstance == null)
        {
            _logger.LogWarning("Failed to create dynamic Moonfin similar items provider.");
            return;
        }

        // Preserve existing registered providers (e.g. built-in Local Genre/Tag) alongside Moonfin Recommends
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var existingField = simMgr.GetType().GetField("_similarItemsProviders", flags);
        var existingArr = existingField?.GetValue(simMgr) as Array;

        var allList = new List<object>();
        var stockList = new List<object>();
        if (existingArr != null)
        {
            foreach (var item in existingArr)
            {
                if (item != null && item != providerInstance && !item.GetType().FullName!.Contains("Moonfin"))
                {
                    allList.Add(item);
                    stockList.Add(item);
                }
            }
        }

        lock (_stockProviders)
        {
            _stockProviders.Clear();
            _stockProviders.AddRange(stockList);
        }

        allList.Add(providerInstance);

        // Register with ISimilarItemsManager.AddParts
        var addPartsMethod = simMgrInterfaceType.GetMethod("AddParts");
        if (addPartsMethod != null)
        {
            var arr = Array.CreateInstance(provBaseType, allList.Count);
            for (var i = 0; i < allList.Count; i++)
            {
                arr.SetValue(allList[i], i);
            }
            addPartsMethod.Invoke(simMgr, [arr]);
            _logger.LogInformation("Successfully registered 'Moonfin Recommends' provider alongside {ExistingCount} existing providers via ISimilarItemsManager.AddParts.", allList.Count - 1);
        }
        else
        {
            _logger.LogWarning("ISimilarItemsManager.AddParts method not found.");
        }

        // Ensure Moonfin Recommends is prioritized by moving to the beginning of internal provider lists if accessible
        PrioritizeProvider(simMgr, providerInstance);
    }

    private void PrioritizeProvider(object simMgr, object providerInstance)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var currType = simMgr.GetType();

            while (currType != null && currType != typeof(object))
            {
                foreach (var field in currType.GetFields(flags))
                {
                    var val = field.GetValue(simMgr);
                    if (val == null) continue;

                    if (val is IList list)
                    {
                        _logger.LogInformation("ISimilarItemsManager field {FieldName} has IList with {Count} elements.", field.Name, list.Count);
                        if (list.Contains(providerInstance))
                        {
                            list.Remove(providerInstance);
                            list.Insert(0, providerInstance);
                            _logger.LogInformation("Prioritized 'Moonfin Recommends' at head of {FieldName} (IList).", field.Name);
                        }
                    }
                    else if (val is Array arr && arr.Length > 0)
                    {
                        _logger.LogInformation("ISimilarItemsManager field {FieldName} has Array with {Count} elements of type {ElemType}.", field.Name, arr.Length, arr.GetType().GetElementType());
                        var elemType = arr.GetType().GetElementType()!;
                        var listType = typeof(List<>).MakeGenericType(elemType);
                        var listObj = (IList)Activator.CreateInstance(listType, arr)!;
                        if (listObj.Contains(providerInstance))
                        {
                            listObj.Remove(providerInstance);
                            listObj.Insert(0, providerInstance);
                            var newArr = Array.CreateInstance(elemType, listObj.Count);
                            listObj.CopyTo(newArr, 0);
                            field.SetValue(simMgr, newArr);
                            _logger.LogInformation("Prioritized 'Moonfin Recommends' at head of {FieldName} (Array).", field.Name);
                        }
                    }
                }

                currType = currType.BaseType;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not adjust provider priority ordering (non-critical).");
        }
    }

    private object? CreateDynamicProvider(Type localProvType, Type provBaseType)
    {
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

        // Explicit parameterless constructor calling base()
        var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var ilCtor = ctor.GetILGenerator();
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        ilCtor.Emit(OpCodes.Ret);

        // string Name { get; } => "Moonfin Recommends"
        var ifacePropName = provBaseType.GetProperty("Name")!;
        var ifaceGetName = ifacePropName.GetGetMethod()!;
        var propName = typeBuilder.DefineProperty("Name", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
        var getName = typeBuilder.DefineMethod("get_Name", propMethodAttrs, typeof(string), Type.EmptyTypes);
        var ilName = getName.GetILGenerator();
        ilName.Emit(OpCodes.Ldstr, "Moonfin Recommends");
        ilName.Emit(OpCodes.Ret);
        propName.SetGetMethod(getName);
        typeBuilder.DefineMethodOverride(getName, ifaceGetName);

        // MetadataPluginType Type { get; }
        var ifacePropType = provBaseType.GetProperty("Type")!;
        var ifaceGetType = ifacePropType.GetGetMethod()!;
        var metaPluginEnumType = ifacePropType.PropertyType;
        var propType = typeBuilder.DefineProperty("Type", PropertyAttributes.None, metaPluginEnumType, Type.EmptyTypes);
        var getType = typeBuilder.DefineMethod("get_Type", propMethodAttrs, metaPluginEnumType, Type.EmptyTypes);
        var ilType = getType.GetILGenerator();
        int enumVal = 9; // Default fallback for LocalSimilarityProvider
        try
        {
            enumVal = Convert.ToInt32(Enum.Parse(metaPluginEnumType, "LocalSimilarityProvider"));
        }
        catch
        {
            // Fallback to integer 9
        }
        ilType.Emit(OpCodes.Ldc_I4, enumVal);
        ilType.Emit(OpCodes.Ret);
        propType.SetGetMethod(getType);
        typeBuilder.DefineMethodOverride(getType, ifaceGetType);

        // TimeSpan? CacheDuration { get; } => 1 day
        var ifacePropCache = provBaseType.GetProperty("CacheDuration")!;
        var ifaceGetCache = ifacePropCache.GetGetMethod()!;
        var nullableTimeSpanType = ifacePropCache.PropertyType;
        var propCache = typeBuilder.DefineProperty("CacheDuration", PropertyAttributes.None, nullableTimeSpanType, Type.EmptyTypes);
        var getCache = typeBuilder.DefineMethod("get_CacheDuration", propMethodAttrs, nullableTimeSpanType, Type.EmptyTypes);
        var ilCache = getCache.GetILGenerator();
        var ctorNullable = nullableTimeSpanType.GetConstructor([typeof(TimeSpan)])!;
        var fromHoursMethod = typeof(TimeSpan).GetMethod("FromHours", [typeof(double)])!;
        ilCache.Emit(OpCodes.Ldc_R8, 24.0);
        ilCache.Emit(OpCodes.Call, fromHoursMethod);
        ilCache.Emit(OpCodes.Newobj, ctorNullable);
        ilCache.Emit(OpCodes.Ret);
        propCache.SetGetMethod(getCache);
        typeBuilder.DefineMethodOverride(getCache, ifaceGetCache);

        // Static delegate fields
        var fSupports = typeBuilder.DefineField("_supportsDelegate", typeof(Func<object, bool>), FieldAttributes.Public | FieldAttributes.Static);
        var fGetSimilar = typeBuilder.DefineField("_getSimilarDelegate", typeof(Func<object, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>), FieldAttributes.Public | FieldAttributes.Static);

        // bool Supports(BaseItem item)
        var ifaceSupports = localProvType.GetMethod("Supports")!;
        var supportsParams = ifaceSupports.GetParameters().Select(p => p.ParameterType).ToArray();
        var mSupports = typeBuilder.DefineMethod("Supports", ifaceMethodAttrs, typeof(bool), supportsParams);
        var ilSupports = mSupports.GetILGenerator();
        ilSupports.Emit(OpCodes.Ldsfld, fSupports);
        ilSupports.Emit(OpCodes.Ldarg_1);
        ilSupports.Emit(OpCodes.Callvirt, typeof(Func<object, bool>).GetMethod("Invoke")!);
        ilSupports.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mSupports, ifaceSupports);

        // Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(BaseItem item, SimilarItemsQuery query, CancellationToken cancellationToken)
        var ifaceGetSimilar = localProvType.GetMethod("GetSimilarItemsAsync")!;
        var getSimilarParams = ifaceGetSimilar.GetParameters().Select(p => p.ParameterType).ToArray();
        var getSimilarReturn = ifaceGetSimilar.ReturnType;
        var mGetSimilar = typeBuilder.DefineMethod("GetSimilarItemsAsync", ifaceMethodAttrs, getSimilarReturn, getSimilarParams);
        var ilGetSimilar = mGetSimilar.GetILGenerator();
        ilGetSimilar.Emit(OpCodes.Ldsfld, fGetSimilar);
        ilGetSimilar.Emit(OpCodes.Ldarg_1);
        ilGetSimilar.Emit(OpCodes.Ldarg_2);
        ilGetSimilar.Emit(OpCodes.Ldarg_3);
        ilGetSimilar.Emit(OpCodes.Callvirt, typeof(Func<object, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>).GetMethod("Invoke")!);
        ilGetSimilar.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mGetSimilar, ifaceGetSimilar);

        var generatedType = typeBuilder.CreateType();
        if (generatedType == null) return null;

        // Hook up handlers
        generatedType.GetField("_supportsDelegate")!.SetValue(null, (Func<object, bool>)HandleSupports);
        generatedType.GetField("_getSimilarDelegate")!.SetValue(
            null,
            (Func<object, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>)HandleGetSimilarAsync);

        return Activator.CreateInstance(generatedType);
    }

    private bool HandleSupports(object? rawItem)
    {
        try
        {
            if (rawItem == null) return false;

            if (rawItem is Type t)
            {
                var supported = t.Name is "Movie" or "Series"
                    || typeof(Movie).IsAssignableFrom(t)
                    || typeof(Series).IsAssignableFrom(t);
                _logger.LogInformation("Moonfin Recommends HandleSupports(Type: {TypeName}): {Supported}", t.FullName, supported);
                return supported;
            }

            var typeName = rawItem.GetType().Name;
            var isSupported = typeName is "Movie" or "Series" || rawItem is Movie or Series;
            _logger.LogInformation("Moonfin Recommends HandleSupports(Instance: {TypeName}): {Supported}", typeName, isSupported);
            return isSupported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Moonfin Recommends HandleSupports");
            return false;
        }
    }

    private async Task<IReadOnlyList<BaseItem>> HandleGetSimilarAsync(
        object? rawItem,
        object? rawQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            if (rawItem is not BaseItem item)
            {
                _logger.LogWarning("Moonfin Recommends HandleGetSimilarAsync called with non-BaseItem: {Type}", rawItem?.GetType().FullName);
                return Array.Empty<BaseItem>();
            }

            // Check if the current HTTP request explicitly requested stock/bypass (e.g. asking specifically for Jellyfin stock engine)
            try
            {
                var httpAccessorType = Type.GetType("Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.Abstractions");
                if (httpAccessorType != null)
                {
                    var accessor = _serviceProvider.GetService(httpAccessorType);
                    if (accessor != null)
                    {
                        var httpContext = accessor.GetType().GetProperty("HttpContext")?.GetValue(accessor);
                        if (httpContext != null)
                        {
                            var req = httpContext.GetType().GetProperty("Request")?.GetValue(httpContext);
                            var queryString = req?.GetType().GetProperty("QueryString")?.GetValue(req)?.ToString() ?? string.Empty;
                            if (queryString.Contains("bypass=moonfin", StringComparison.OrdinalIgnoreCase) ||
                                queryString.Contains("engine=jellyfin", StringComparison.OrdinalIgnoreCase) ||
                                queryString.Contains("engine=stock", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("Moonfin Recommends bypassed via query parameter for '{ItemName}'. Invoking stock provider directly.", item.Name);
                                var stockResults = await InvokeStockProviderAsync(item, rawQuery, cancellationToken).ConfigureAwait(false);
                                return stockResults;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not check HttpContext for bypass parameter.");
            }

            _logger.LogInformation("Moonfin Recommends HandleGetSimilarAsync invoked for '{ItemName}' ({ItemId}).", item.Name, item.Id);

            object? user = null;
            int? limit = null;
            IReadOnlyList<Guid>? excludeItemIds = null;

            if (rawQuery != null)
            {
                var qType = rawQuery.GetType();
                user = qType.GetProperty("User")?.GetValue(rawQuery);
                limit = qType.GetProperty("Limit")?.GetValue(rawQuery) as int?;
                excludeItemIds = qType.GetProperty("ExcludeItemIds")?.GetValue(rawQuery) as IReadOnlyList<Guid>;
            }

            var results = await _similarItemsService.GetSimilarItemsAsync(
                item,
                user,
                limit,
                excludeItemIds,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Moonfin Recommends returning {Count} recommendations for '{ItemName}'.", results.Count, item.Name);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating similar items in provider.");
            return Array.Empty<BaseItem>();
        }
    }

    private async Task<IReadOnlyList<BaseItem>> InvokeStockProviderAsync(
        BaseItem item,
        object? rawQuery,
        CancellationToken cancellationToken)
    {
        List<object> providers;
        lock (_stockProviders)
        {
            providers = new List<object>(_stockProviders);
        }

        // Boost candidate limit for stock provider so internal Take(limit) covers entire library
        if (rawQuery != null)
        {
            try
            {
                var limitProp = rawQuery.GetType().GetProperty("Limit");
                if (limitProp != null && limitProp.CanWrite)
                {
                    var currentLimit = limitProp.GetValue(rawQuery) as int?;
                    if (currentLimit == null || currentLimit < 5000)
                    {
                        limitProp.SetValue(rawQuery, 5000);
                    }
                }
            }
            catch { }
        }

        var itemType = item.GetType();
        foreach (var sp in providers)
        {
            try
            {
                var spType = sp.GetType();
                var localProvType = Type.GetType("MediaBrowser.Controller.Library.ILocalSimilarItemsProvider, MediaBrowser.Controller");
                var supportsMethod = localProvType != null
                    ? localProvType.GetMethod("Supports")
                    : spType.GetMethod("Supports", [typeof(Type)]);

                var isSupported = false;
                if (supportsMethod != null)
                {
                    var res = supportsMethod.Invoke(sp, [itemType]);
                    if (res is bool b && b)
                    {
                        isSupported = true;
                    }
                }
                else
                {
                    var directSupports = spType.GetMethod("Supports", [typeof(BaseItem)])
                        ?? spType.GetMethod("Supports", [itemType]);
                    if (directSupports != null)
                    {
                        var res = directSupports.Invoke(sp, [item]);
                        if (res is bool b && b)
                        {
                            isSupported = true;
                        }
                    }
                }

                if (isSupported)
                {
                    var getSimMethod = localProvType != null
                        ? localProvType.GetMethod("GetSimilarItemsAsync")
                        : spType.GetMethod("GetSimilarItemsAsync");

                    if (getSimMethod != null)
                    {
                        var taskObj = getSimMethod.Invoke(sp, [item, rawQuery, cancellationToken]);
                        if (taskObj is Task<IReadOnlyList<BaseItem>> typedTask)
                        {
                            var list = await typedTask.ConfigureAwait(false);
                            _logger.LogInformation("Stock provider {ProviderName} returned {Count} items for '{ItemName}'.", spType.Name, list.Count, item.Name);
                            return list;
                        }
                        if (taskObj is Task genericTask)
                        {
                            await genericTask.ConfigureAwait(false);
                            var prop = genericTask.GetType().GetProperty("Result");
                            if (prop?.GetValue(genericTask) is IReadOnlyList<BaseItem> list)
                            {
                                _logger.LogInformation("Stock provider {ProviderName} returned {Count} items for '{ItemName}'.", spType.Name, list.Count, item.Name);
                                return list;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invoke stock similarity provider {ProviderType} for '{ItemName}'.", sp.GetType().FullName, item.Name);
            }
        }

        return Array.Empty<BaseItem>();
    }
}
