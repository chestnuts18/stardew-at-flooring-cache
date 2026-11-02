# AT Flooring Cache (Prototype)

A SMAPI mod for **Stardew Valley 1.6** that caches **Alternative Textures (AT)** flooring and idle big craftables into per-tile sprites, reducing per-frame draw calls.

> 中文简介：把 AT 地板和空闲的大制作品预渲染成小图，每帧只画一次，降低 AT 的逐块/逐件绘制开销。**动画优先**：正在加工、已完成、AT 帧动画、灯、旋转物、震动、事件中的对象一律不缓存。

## Features

- **Flooring cache** — caches AT flooring tiles into 72×72 sprites (corners + shadow + main tile in one draw).
- **Big craftable cache** — caches idle/static AT big craftables (kegs, jars, machines) into 256×256 sprites.
- **Animation-first policy** — active machines, ready products, AT texture frame animations, lamps, rotating props, shaking objects, and event objects are never cached.
- **Prewarm on location change** — visible flooring tiles are queued when entering a location.
- **Time-budgeted generation** — `CacheBudgetMs` prevents cache generation from causing large hitches.
- **GMCM support** — configuration page via Generic Mod Config Menu.
- **Diagnostics** — optional `AT floor cache` / `AT object cache` stats in the SMAPI console.

## Requirements

- Stardew Valley **1.6.15** (tested) or later 1.6.x
- SMAPI **4.5.2** (tested)
- [Alternative Textures](https://www.nexusmods.com/stardewvalley/mods/9246) **8.1.1** (tested)
- .NET **6.0 SDK** to build

## Build

```powershell
dotnet build ATFlooringCache.csproj -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
```

The project uses a `GamePath` MSBuild property for the Stardew Valley install folder. It defaults to the standard Steam path; override it with `-p:GamePath=...` or a local `Directory.Build.props` if your game is elsewhere.

## Install

Copy these files into `Stardew Valley/Mods/ATFlooringCache/`:

- `ATFlooringCache.dll`
- `manifest.json`
- `config.json`

The mod requires **Alternative Textures** and will not enable its patches without it.

## Configuration

`config.json` (or GMCM):

| Option | Default | Description |
|---|---:|---|
| `Enabled` | `true` | Master switch. |
| `CachesPerTick` | `8` | Max cache entries generated per tick. |
| `MaxCacheEntries` | `1024` | Flooring cache sprite cap. |
| `CacheBudgetMs` | `4` | Max milliseconds per tick spent generating cache. |
| `PrewarmOnLocationChange` | `true` | Queue visible flooring when entering a location. |
| `LogStats` | `true` | Print cache statistics every `StatsIntervalSeconds`. |
| `StatsIntervalSeconds` | `10` | Statistics interval. |
| `EnableObjectCache` | `true` | Enable the big-craftable cache. |
| `MaxObjectCacheEntries` | `2048` | Big-craftable cache sprite cap. |

## Animation Policy

This mod deliberately **does not cache animated states**:

- `MinutesUntilReady > 0` (brewing/processing — e.g. keg deformation)
- `readyForHarvest` (finished product bubble / held item)
- `AlternativeTextureCurrentFrame` (AT texture frame animations)
- `isLamp`, `ParentSheetIndex == 272` (rotating), `shakeTimer > 0`, during events
- Animated flooring

Only idle/static states are cached. This keeps AT animations intact at the cost of some performance.

## Current Limitations

- Prototype; tested on a heavily modded 1.6.15 save.
- Only **AT flooring** and **idle big craftables** are cached.
- Buildings, furniture, chests, special objects, regular small objects, terrain/vegetation, animals, and NPCs are **not** cached.
- Buildings are intentionally skipped: Alternative Textures only swaps the building texture; the actual draw is vanilla `Building.draw`, which is multi-layer and animation-heavy.
- AT internals may change; the patches may fail on future AT versions. Check the SMAPI log for `Patched ...DrawPrefix` lines.
- No save data is modified; removing the mod restores normal AT drawing. Back up saves anyway.

## Credits

- **Alternative Textures** by PeacefulEnd / Floogen — the framework this mod targets.
- **SMAPI** by Pathoschild.
- **Generic Mod Config Menu** by spacechase0.
- Stardew Valley by ConcernedApe.

This is an independent prototype. It uses Harmony patches and reflection against Alternative Textures; it does not redistribute AT code or assets.

## License

No license has been chosen yet. All rights reserved by the author until a license is added. If you want others to reuse the code, add a license (e.g. MIT) to this repository.
