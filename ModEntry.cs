using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.FloorsAndPaths;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SDObject = StardewValley.Object;
using XRectangle = xTile.Dimensions.Rectangle;

namespace ATFlooringCache;

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public int CachesPerTick { get; set; } = 8;
    public int MaxCacheEntries { get; set; } = 1024;
    public int CacheBudgetMs { get; set; } = 4;
    public bool PrewarmOnLocationChange { get; set; } = true;
    public bool LogStats { get; set; } = true;
    public int StatsIntervalSeconds { get; set; } = 10;
    public bool EnableObjectCache { get; set; } = true;
    public int MaxObjectCacheEntries { get; set; } = 2048;
}

public sealed class ModEntry : Mod
{
    private const string TextureNameKey = "AlternativeTextureName";
    private const string TextureVariationKey = "AlternativeTextureVariation";
    private const int TilePixels = 64;
    private const int CacheMargin = 4;
    private const int CacheSize = TilePixels + CacheMargin * 2;

    internal static ModEntry? Instance;

    private ModConfig Config = null!;
    private Harmony? HarmonyInstance;
    private MethodInfo? DrawPrefixMethod;
    private bool PatchOk;
    private bool Generating;
    private bool ClearCacheRequested;
    private int CacheBuilds;
    private long CacheHits;
    private long CacheQueued;
    private double CacheBuildMs;
    private long LastStatsTick;
    private static FieldInfo? NeighborMaskField;

    private MethodInfo? ObjectDrawPrefixMethod;
    private bool ObjectPatchOk;
    private int ObjectCacheBuilds;
    private long ObjectCacheHits;
    private long ObjectCacheQueued;
    private double ObjectCacheBuildMs;

    private readonly Dictionary<CacheKey, RenderTarget2D> Cache = new();
    private readonly HashSet<Flooring> PendingSet = new();
    private readonly Queue<PendingItem> PendingQueue = new();
    private readonly HashSet<CacheKey> FailedKeys = new();
    private readonly HashSet<GameLocation> PrewarmedLocations = new();

    private readonly Dictionary<ObjectCacheKey, RenderTarget2D> ObjectCache = new();
    private readonly HashSet<SDObject> ObjectPendingSet = new();
    private readonly Queue<ObjectPendingItem> ObjectPendingQueue = new();
    private readonly HashSet<ObjectCacheKey> ObjectFailedKeys = new();
    private readonly HashSet<string> ObservedObjectScales = new();

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        this.Monitor.Log("AT Flooring Cache prototype loaded. Waiting to patch Alternative Textures...", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.RegisterGenericModConfigMenu();
        this.PatchObjectCache();

        Type? flooringPatchType = AccessTools.TypeByName("AlternativeTextures.Framework.Patches.StandardObjects.FlooringPatch");
        if (flooringPatchType is null)
        {
            this.Monitor.Log("AlternativeTextures FlooringPatch type not found; prototype disabled.", LogLevel.Error);
            return;
        }

        DrawPrefixMethod = AccessTools.Method(flooringPatchType, "DrawPrefix");
        if (DrawPrefixMethod is null)
        {
            this.Monitor.Log("AlternativeTextures FlooringPatch.DrawPrefix not found; prototype disabled.", LogLevel.Error);
            return;
        }

        try
        {
            HarmonyInstance ??= new Harmony(this.ModManifest.UniqueID);
            HarmonyInstance.Patch(
                DrawPrefixMethod,
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(CachedDrawPrefix)) { priority = Priority.First }
            );
            PatchOk = true;
            NeighborMaskField = AccessTools.Field(typeof(Flooring), "neighborMask");

            string parameters = string.Join(", ", DrawPrefixMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            this.Monitor.Log($"Patched {flooringPatchType.FullName}.DrawPrefix({parameters}).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to patch AT flooring draw: {ex}", LogLevel.Error);
        }
    }

    private void PatchObjectCache()
    {
        Type? objectPatchType = AccessTools.TypeByName("AlternativeTextures.Framework.Patches.StandardObjects.ObjectPatch");
        if (objectPatchType is null)
        {
            this.Monitor.Log("AlternativeTextures ObjectPatch type not found; object cache disabled.", LogLevel.Warn);
            return;
        }

        ObjectDrawPrefixMethod = AccessTools.Method(objectPatchType, "DrawPrefix");
        if (ObjectDrawPrefixMethod is null)
        {
            this.Monitor.Log("AlternativeTextures ObjectPatch.DrawPrefix not found; object cache disabled.", LogLevel.Warn);
            return;
        }

        try
        {
            HarmonyInstance ??= new Harmony(this.ModManifest.UniqueID);
            HarmonyInstance.Patch(
                ObjectDrawPrefixMethod,
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(CachedObjectDrawPrefix)) { priority = Priority.First }
            );

            ObjectPatchOk = true;
            string parameters = string.Join(", ", ObjectDrawPrefixMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            this.Monitor.Log($"Patched {objectPatchType.FullName}.DrawPrefix({parameters}).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to patch AT object draw: {ex}", LogLevel.Error);
        }
    }

    private static bool CachedDrawPrefix(object[] __args)
    {
        ModEntry? self = Instance;
        if (self is null || !self.Config.Enabled || self.Generating || !self.PatchOk)
            return true;

        if (__args.Length < 3
            || __args[0] is not Flooring flooring
            || __args[1] is not byte neighborMask
            || __args[2] is not SpriteBatch spriteBatch)
            return true;

        if (!flooring.modData.TryGetValue(TextureNameKey, out string? textureName) || string.IsNullOrEmpty(textureName))
            return true;

        if (!flooring.modData.TryGetValue(TextureVariationKey, out string? variationText)
            || !int.TryParse(variationText, out int variation)
            || variation < 0)
            return true;

        // Never cache animated AT flooring.
        if (flooring.modData.ContainsKey("AlternativeTextureCurrentFrame"))
            return true;

        var data = flooring.GetData();
        int view = flooring.whichView.Value;
        string floorId = flooring.whichFloor.Value ?? "";
        var key = new CacheKey(textureName, variation, neighborMask, data.ConnectType, data.CornerSize, data.ShadowType, view, floorId);

        if (self.FailedKeys.Contains(key))
            return true;

        if (self.Cache.TryGetValue(key, out RenderTarget2D? cached))
        {
            self.CacheHits++;
            Vector2 tile = flooring.Tile;
            Vector2 position = new(
                tile.X * TilePixels - Game1.viewport.X - CacheMargin,
                tile.Y * TilePixels - Game1.viewport.Y - CacheMargin
            );

            spriteBatch.Draw(cached, position, null, Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 1e-09f);
            return false;
        }

        if (self.PendingSet.Add(flooring))
        {
            self.CacheQueued++;
            self.PendingQueue.Enqueue(new PendingItem(flooring, neighborMask));
        }

        return true;
    }

    private static bool CachedObjectDrawPrefix(object[] __args)
    {
        ModEntry? self = Instance;
        if (self is null || !self.Config.Enabled || !self.Config.EnableObjectCache || self.Generating || !self.ObjectPatchOk)
            return true;

        if (__args.Length < 5
            || __args[0] is not SDObject obj
            || __args[1] is not SpriteBatch spriteBatch
            || __args[2] is not int x
            || __args[3] is not int y
            || __args[4] is not float alpha)
            return true;

        if (!obj.bigCraftable.Value || alpha != 1f || obj.shakeTimer > 0 || Game1.eventUp)
            return true;

        if (obj.isLamp.Value || obj.ParentSheetIndex == 272)
            return true;

        // Ready machines/products have highly variable held-item data and a bobbing
        // bubble; caching them caused thousands of unique keys and cache thrashing.
        if (obj.readyForHarvest.Value)
            return true;

        // Active machines animate their scale (e.g. keg brewing deformation).
        // Let AT draw those live; only cache idle/empty machines.
        if (obj.MinutesUntilReady > 0)
            return true;

        if (obj.modData.ContainsKey("AlternativeTextureCurrentFrame"))
            return true;

        if (!obj.modData.TryGetValue(TextureNameKey, out string? textureName) || string.IsNullOrEmpty(textureName))
            return true;

        if (!obj.modData.TryGetValue(TextureVariationKey, out string? variationText)
            || !int.TryParse(variationText, out int variation)
            || variation < 0)
            return true;

        ObjectCacheKey key = BuildObjectCacheKey(obj, textureName, variation);

        if (self.ObjectFailedKeys.Contains(key))
            return true;

        if (self.ObjectCache.TryGetValue(key, out RenderTarget2D? cached))
        {
            self.ObjectCacheHits++;
            Vector2 position = new(
                x * 64 - Game1.viewport.X - 64,
                y * 64 - Game1.viewport.Y - 128
            );

            spriteBatch.Draw(cached, position, null, Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, GetBigCraftableLayerDepth(obj, x, y));
            return false;
        }

        if (self.ObjectPendingSet.Add(obj))
        {
            self.ObjectCacheQueued++;
            self.ObjectPendingQueue.Enqueue(new ObjectPendingItem(obj, x, y));
        }

        return true;
    }

    private static ObjectCacheKey BuildObjectCacheKey(SDObject obj, string textureName, int variation)
    {
        return new ObjectCacheKey(
            textureName,
            variation,
            obj.ParentSheetIndex,
            obj.showNextIndex.Value,
            obj.Quality,
            obj.Flipped,
            obj.MinutesUntilReady > 0
        );
    }

    private static float GetBigCraftableLayerDepth(SDObject obj, int x, int y)
    {
        float drawLayer = Math.Max(0f, ((y + 1) * 64 - 24) / 10000f) + x * 1e-5f;
        if (obj.ParentSheetIndex == 105 || obj.ParentSheetIndex == 264)
            drawLayer = Math.Max(0f, ((y + 1) * 64 + 2) / 10000f) + x / 1000000f;
        return drawLayer;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.ClearCacheRequested)
        {
            this.ClearCacheRequested = false;
            this.ClearCacheNow();
        }

        if (!this.Config.Enabled || this.Generating)
            return;

        this.PrewarmIfNeeded();
        this.LogStatsIfDue(e.Ticks);

        if (this.PatchOk)
        {
        Stopwatch budgetWatch = Stopwatch.StartNew();
        int budget = Math.Max(1, this.Config.CachesPerTick);

        while (budget-- > 0 && this.PendingQueue.Count > 0)
        {
            PendingItem item = this.PendingQueue.Dequeue();
            this.PendingSet.Remove(item.Flooring);

            Flooring flooring = item.Flooring;
            if (flooring.Location != Game1.currentLocation)
                continue;

            if (!flooring.modData.TryGetValue(TextureNameKey, out string? textureName) || string.IsNullOrEmpty(textureName))
                continue;

            if (!flooring.modData.TryGetValue(TextureVariationKey, out string? variationText)
                || !int.TryParse(variationText, out int variation)
                || variation < 0)
                continue;

            var data = flooring.GetData();
            int view = flooring.whichView.Value;
            string floorId = flooring.whichFloor.Value ?? "";
            var key = new CacheKey(textureName, variation, item.Mask, data.ConnectType, data.CornerSize, data.ShadowType, view, floorId);

            if (this.Cache.ContainsKey(key) || this.FailedKeys.Contains(key))
                continue;

            long buildStart = Stopwatch.GetTimestamp();
            RenderTarget2D? rendered = this.Generate(key, flooring, item.Mask);
            this.CacheBuildMs += (Stopwatch.GetTimestamp() - buildStart) * 1000.0 / Stopwatch.Frequency;

            if (rendered is null)
            {
                this.FailedKeys.Add(key);
                continue;
            }

            this.Cache[key] = rendered;
            this.CacheBuilds++;

            if (this.CacheBuilds == 1 || this.CacheBuilds % 100 == 0)
                this.Monitor.Log($"AT flooring cache entries: {this.Cache.Count} (built {this.CacheBuilds}).", LogLevel.Info);

            this.TrimCache();

            if (budgetWatch.Elapsed.TotalMilliseconds >= Math.Max(1, this.Config.CacheBudgetMs))
                break;
        }
        }

        this.ProcessObjectCache();
    }

    private void ProcessObjectCache()
    {
        if (!this.Config.EnableObjectCache || !this.ObjectPatchOk || this.ObjectPendingQueue.Count == 0)
            return;

        Stopwatch watch = Stopwatch.StartNew();
        int budget = Math.Max(1, this.Config.CachesPerTick);

        while (budget-- > 0 && this.ObjectPendingQueue.Count > 0)
        {
            ObjectPendingItem item = this.ObjectPendingQueue.Dequeue();
            this.ObjectPendingSet.Remove(item.Object);
            SDObject obj = item.Object;

            if (!obj.bigCraftable.Value || obj.shakeTimer > 0 || obj.isLamp.Value || Game1.eventUp)
                continue;

            if (obj.ParentSheetIndex == 272
                || obj.readyForHarvest.Value
                || obj.MinutesUntilReady > 0
                || obj.modData.ContainsKey("AlternativeTextureCurrentFrame"))
                continue;

            if (!obj.modData.TryGetValue(TextureNameKey, out string? textureName) || string.IsNullOrEmpty(textureName))
                continue;

            if (!obj.modData.TryGetValue(TextureVariationKey, out string? variationText)
                || !int.TryParse(variationText, out int variation)
                || variation < 0)
                continue;

            Vector2 scale = obj.getScale();
            this.ObservedObjectScales.Add($"{scale.X:F3}x{scale.Y:F3}");

            ObjectCacheKey key = BuildObjectCacheKey(obj, textureName, variation);
            if (this.ObjectCache.ContainsKey(key) || this.ObjectFailedKeys.Contains(key))
                continue;

            long buildStart = Stopwatch.GetTimestamp();
            RenderTarget2D? rendered = this.GenerateObject(key, obj, item.X, item.Y);
            this.ObjectCacheBuildMs += (Stopwatch.GetTimestamp() - buildStart) * 1000.0 / Stopwatch.Frequency;

            if (rendered is null)
            {
                this.ObjectFailedKeys.Add(key);
                continue;
            }

            this.ObjectCache[key] = rendered;
            this.ObjectCacheBuilds++;

            if (this.ObjectCacheBuilds == 1 || this.ObjectCacheBuilds % 250 == 0)
                this.Monitor.Log($"AT object cache entries: {this.ObjectCache.Count} (built {this.ObjectCacheBuilds}).", LogLevel.Info);

            this.TrimObjectCache();

            if (watch.Elapsed.TotalMilliseconds >= Math.Max(1, this.Config.CacheBudgetMs))
                break;
        }
    }

    private RenderTarget2D? GenerateObject(ObjectCacheKey key, SDObject obj, int x, int y)
    {
        if (this.ObjectDrawPrefixMethod is null)
            return null;

        GraphicsDevice device = Game1.graphics.GraphicsDevice;
        XRectangle oldViewport = Game1.viewport;
        RenderTargetBinding[] oldTargets = device.GetRenderTargets();
        RenderTarget2D? target = null;

        try
        {
            target = new RenderTarget2D(
                device,
                256,
                256,
                mipMap: false,
                SurfaceFormat.Color,
                DepthFormat.None,
                preferredMultiSampleCount: 0,
                RenderTargetUsage.PreserveContents
            );

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);
            Game1.viewport = new XRectangle(x * 64 - 64, y * 64 - 128, 256, 256);

            this.Generating = true;
            using (var batch = new SpriteBatch(device))
            {
                batch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.AlphaBlend,
                    SamplerState.PointClamp,
                    DepthStencilState.None,
                    RasterizerState.CullNone
                );

                object?[] args = this.BuildObjectArguments(obj, batch, x, y);
                object? result = this.ObjectDrawPrefixMethod.Invoke(null, args);

                batch.End();

                if (result is true)
                {
                    target.Dispose();
                    return null;
                }
            }

            return target;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to build AT object cache for {key}: {ex}", LogLevel.Error);
            target?.Dispose();
            return null;
        }
        finally
        {
            this.Generating = false;
            Game1.viewport = oldViewport;
            device.SetRenderTarget(oldTargets.Length > 0 ? oldTargets[0].RenderTarget as RenderTarget2D : null);
        }
    }

    private object?[] BuildObjectArguments(SDObject obj, SpriteBatch batch, int x, int y)
    {
        ParameterInfo[] parameters = this.ObjectDrawPrefixMethod!.GetParameters();
        object?[] args = new object?[parameters.Length];
        int intIndex = 0;

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(SDObject))
                args[i] = obj;
            else if (type == typeof(SpriteBatch))
                args[i] = batch;
            else if (type == typeof(int))
            {
                args[i] = intIndex == 0 ? x : y;
                intIndex++;
            }
            else if (type == typeof(float))
                args[i] = 1f;
            else
                args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        return args;
    }

    private void TrimObjectCache()
    {
        while (this.ObjectCache.Count > Math.Max(1, this.Config.MaxObjectCacheEntries))
        {
            ObjectCacheKey firstKey = this.ObjectCache.Keys.First();
            RenderTarget2D target = this.ObjectCache[firstKey];
            this.ObjectCache.Remove(firstKey);
            target.Dispose();
        }
    }

    private void PrewarmIfNeeded()
    {
        if (!this.Config.PrewarmOnLocationChange)
            return;

        GameLocation? location = Game1.currentLocation;
        if (location is null || this.PrewarmedLocations.Contains(location))
            return;

        this.PrewarmedLocations.Add(location);

        if (NeighborMaskField is null)
            return;

        int queued = 0;
        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is not Flooring flooring)
                continue;

            if (!flooring.modData.ContainsKey(TextureNameKey) || !flooring.modData.ContainsKey(TextureVariationKey))
                continue;

            if (!this.PendingSet.Add(flooring))
                continue;

            byte mask = 0;
            if (NeighborMaskField.GetValue(flooring) is byte value)
                mask = value;

            this.PendingQueue.Enqueue(new PendingItem(flooring, mask));
            queued++;
        }

        this.CacheQueued += queued;

        if (queued > 0)
            this.Monitor.Log($"Pre-warmed {queued} AT flooring tiles in {location.Name}.", LogLevel.Trace);
    }

    private void LogStatsIfDue(long ticks)
    {
        if (!this.Config.LogStats)
            return;

        long interval = Math.Max(1, this.Config.StatsIntervalSeconds) * 60L;
        if (ticks - this.LastStatsTick < interval)
            return;

        bool hasFloor = this.CacheBuilds > 0 || this.CacheHits > 0 || this.CacheQueued > 0;
        bool hasObject = this.ObjectCacheBuilds > 0 || this.ObjectCacheHits > 0 || this.ObjectCacheQueued > 0;
        if (!hasFloor && !hasObject)
            return;

        this.LastStatsTick = ticks;

        if (hasFloor)
        {
            double averageBuild = this.CacheBuilds > 0 ? this.CacheBuildMs / this.CacheBuilds : 0;
            this.Monitor.Log(
                $"AT floor cache: entries={this.Cache.Count}, built={this.CacheBuilds}, hits={this.CacheHits}, queued={this.CacheQueued}, pending={this.PendingQueue.Count}, avgBuild={averageBuild:F2}ms",
                LogLevel.Info
            );
        }

        if (hasObject)
        {
            double averageBuild = this.ObjectCacheBuilds > 0 ? this.ObjectCacheBuildMs / this.ObjectCacheBuilds : 0;
            this.Monitor.Log(
                $"AT object cache: entries={this.ObjectCache.Count}, built={this.ObjectCacheBuilds}, hits={this.ObjectCacheHits}, queued={this.ObjectCacheQueued}, pending={this.ObjectPendingQueue.Count}, avgBuild={averageBuild:F2}ms",
                LogLevel.Info
            );

            this.LogObjectKeyBreakdown();
        }
    }

    private void LogObjectKeyBreakdown()
    {
        if (this.ObjectCache.Count == 0)
            return;

        var keys = this.ObjectCache.Keys;
        int textures = keys.Select(k => k.TextureName).Distinct().Count();
        int variations = keys.Select(k => k.Variation).Distinct().Count();
        int parents = keys.Select(k => k.ParentSheetIndex).Distinct().Count();
        int shows = keys.Select(k => k.ShowNextIndex).Distinct().Count();
        int qualities = keys.Select(k => k.Quality).Distinct().Count();
        int flipped = keys.Select(k => k.Flipped).Distinct().Count();
        int active = keys.Select(k => k.MachineActive).Distinct().Count();

        this.Monitor.Log(
            $"AT object key fields: textures={textures}, variations={variations}, parent={parents}, show={shows}, quality={qualities}, flipped={flipped}, active={active}, observedScales={this.ObservedObjectScales.Count}",
            LogLevel.Info
        );
    }

    private RenderTarget2D? Generate(CacheKey key, Flooring flooring, byte mask)
    {
        if (this.DrawPrefixMethod is null)
            return null;

        GraphicsDevice device = Game1.graphics.GraphicsDevice;
        XRectangle oldViewport = Game1.viewport;
        RenderTargetBinding[] oldTargets = device.GetRenderTargets();
        RenderTarget2D? target = null;

        try
        {
            target = new RenderTarget2D(
                device,
                CacheSize,
                CacheSize,
                mipMap: false,
                SurfaceFormat.Color,
                DepthFormat.None,
                preferredMultiSampleCount: 0,
                RenderTargetUsage.PreserveContents
            );

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            Vector2 tile = flooring.Tile;
            Game1.viewport = new XRectangle(
                (int)(tile.X * TilePixels) - CacheMargin,
                (int)(tile.Y * TilePixels) - CacheMargin,
                CacheSize,
                CacheSize
            );

            this.Generating = true;
            using (var batch = new SpriteBatch(device))
            {
                batch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.AlphaBlend,
                    SamplerState.PointClamp,
                    DepthStencilState.None,
                    RasterizerState.CullNone
                );

                object?[] args = this.BuildArguments(flooring, mask, batch);
                object? result = this.DrawPrefixMethod.Invoke(null, args);

                batch.End();

                // AT's prefix returns true when it did not draw (missing/disabled texture).
                // In that case, do not cache a blank sprite; let AT/vanilla handle the tile.
                if (result is true)
                {
                    target.Dispose();
                    return null;
                }
            }

            return target;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to build AT flooring cache for {key}: {ex}", LogLevel.Error);
            target?.Dispose();
            return null;
        }
        finally
        {
            this.Generating = false;
            Game1.viewport = oldViewport;
            device.SetRenderTarget(oldTargets.Length > 0 ? oldTargets[0].RenderTarget as RenderTarget2D : null);
        }
    }

    private object?[] BuildArguments(Flooring flooring, byte mask, SpriteBatch batch)
    {
        ParameterInfo[] parameters = this.DrawPrefixMethod!.GetParameters();
        object?[] args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(Flooring))
                args[i] = flooring;
            else if (type == typeof(byte))
                args[i] = mask;
            else if (type == typeof(SpriteBatch))
                args[i] = batch;
            else
                args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        return args;
    }

    private void TrimCache()
    {
        while (this.Cache.Count > Math.Max(1, this.Config.MaxCacheEntries))
        {
            CacheKey firstKey = this.Cache.Keys.First();
            RenderTarget2D target = this.Cache[firstKey];
            this.Cache.Remove(firstKey);
            target.Dispose();
        }
    }

    private void RegisterGenericModConfigMenu()
    {
        IGenericModConfigMenuApi? api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
        {
            this.Monitor.Log("Generic Mod Config Menu not found; edit config.json instead.", LogLevel.Trace);
            return;
        }

        api.Register(this.ModManifest, this.ResetConfig, this.SaveConfig);

        api.AddBoolOption(
            this.ModManifest,
            () => this.Config.Enabled,
            value => this.Config.Enabled = value,
            () => "启用",
            () => "总开关；关闭后恢复 Alternative Textures 原始绘制。"
        );

        api.AddBoolOption(
            this.ModManifest,
            () => this.Config.EnableObjectCache,
            value => this.Config.EnableObjectCache = value,
            () => "制作品缓存",
            () => "缓存 AT 大制作品（Keg、Jar、机器等）的绘制；动画纹理和灯暂不缓存。"
        );

        api.AddNumberOption(
            this.ModManifest,
            () => this.Config.MaxObjectCacheEntries,
            value => this.Config.MaxObjectCacheEntries = value,
            () => "制作品缓存上限",
            () => "制作品缓存 sprite 上限；每条 256×256，约 256KB。",
            min: 32,
            max: 4096,
            interval: 32
        );

        api.AddNumberOption(
            this.ModManifest,
            () => this.Config.CachesPerTick,
            value => this.Config.CachesPerTick = value,
            () => "每 tick 生成数量",
            () => "每帧最多生成多少个地板缓存。越大预热越快，但可能造成小卡顿。",
            min: 1,
            max: 32,
            interval: 1
        );

        api.AddNumberOption(
            this.ModManifest,
            () => this.Config.CacheBudgetMs,
            value => this.Config.CacheBudgetMs = value,
            () => "每 tick 时间预算 (ms)",
            () => "每帧用于生成缓存的时间上限；进区域卡顿就调低。",
            min: 1,
            max: 16,
            interval: 1
        );

        api.AddNumberOption(
            this.ModManifest,
            () => this.Config.MaxCacheEntries,
            value => this.Config.MaxCacheEntries = value,
            () => "最大缓存条目",
            () => "缓存 sprite 上限；1024 条约占 20MB 显存。",
            min: 64,
            max: 8192,
            interval: 64
        );

        api.AddBoolOption(
            this.ModManifest,
            () => this.Config.PrewarmOnLocationChange,
            value => this.Config.PrewarmOnLocationChange = value,
            () => "进入区域时预热",
            () => "进入 Farm/小屋时，先把当前区域的 AT 地板排队生成缓存。"
        );

        api.AddBoolOption(
            this.ModManifest,
            () => this.Config.LogStats,
            value => this.Config.LogStats = value,
            () => "输出统计日志",
            () => "每 N 秒向 SMAPI 控制台输出 entries/hits/pending 统计。"
        );

        api.AddNumberOption(
            this.ModManifest,
            () => this.Config.StatsIntervalSeconds,
            value => this.Config.StatsIntervalSeconds = value,
            () => "统计间隔 (秒)",
            () => "多久输出一次缓存统计。",
            min: 1,
            max: 60,
            interval: 1
        );

        this.Monitor.Log("Registered GMCM config page.", LogLevel.Trace);
    }

    private void ResetConfig()
    {
        this.Config = new ModConfig();
        this.Helper.WriteConfig(this.Config);
        this.ClearCacheRequested = true;
        this.PrewarmedLocations.Clear();
        this.LastStatsTick = 0;
    }

    private void SaveConfig()
    {
        this.Helper.WriteConfig(this.Config);
        this.PrewarmedLocations.Clear();
        this.LastStatsTick = 0;
    }

    private void ClearCacheNow()
    {
        foreach (RenderTarget2D target in this.Cache.Values)
            target.Dispose();
        foreach (RenderTarget2D target in this.ObjectCache.Values)
            target.Dispose();

        this.Cache.Clear();
        this.PendingQueue.Clear();
        this.PendingSet.Clear();
        this.FailedKeys.Clear();
        this.PrewarmedLocations.Clear();
        this.CacheBuilds = 0;
        this.CacheHits = 0;
        this.CacheQueued = 0;
        this.CacheBuildMs = 0;

        this.ObjectCache.Clear();
        this.ObjectPendingQueue.Clear();
        this.ObjectPendingSet.Clear();
        this.ObjectFailedKeys.Clear();
        this.ObservedObjectScales.Clear();
        this.ObjectCacheBuilds = 0;
        this.ObjectCacheHits = 0;
        this.ObjectCacheQueued = 0;
        this.ObjectCacheBuildMs = 0;

        this.LastStatsTick = 0;
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.ClearCacheRequested = false;
        this.ClearCacheNow();
    }

    private readonly record struct PendingItem(Flooring Flooring, byte Mask);

    private readonly record struct CacheKey(
        string TextureName,
        int Variation,
        byte Mask,
        FloorPathConnectType ConnectType,
        int CornerSize,
        FloorPathShadowType ShadowType,
        int View,
        string FloorId
    );

    private readonly record struct ObjectPendingItem(SDObject Object, int X, int Y);

    private readonly record struct ObjectCacheKey(
        string TextureName,
        int Variation,
        int ParentSheetIndex,
        bool ShowNextIndex,
        int Quality,
        bool Flipped,
        bool MachineActive
    );
}
