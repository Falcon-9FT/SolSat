# SolSat

**A mod that reworks the Sol planet pack.** It turns Earth from the Sol planet pack for Kerbal Space
Program 1.12.5 into a Microsoft Flight Simulator-style map: satellite imagery under your feet,
buildings and roads from OpenStreetMap, street lights at night, real terrain.

License: [GPL-3.0](LICENSE) · [Русская версия](README.ru.md)

---

## ⚠️ Warning

By default the mod fetches imagery **directly from Google's servers**, tile by tile. Google's terms
of service do not allow this. With a large number of requests Google may show a captcha, throttle
or block your IP address, including in its other services.

**The mod's author accepts no liability whatsoever** for blocks, your IP address ending up on
spam lists, or any other consequences of downloading satellite imagery. You use the mod at your
own risk.

- Do not raise the download rate above the default: 6 requests per second, 4 connections. If the
  server starts refusing, the mod pauses on its own (from one minute to 15 minutes), and after
  repeated refusals it turns downloading off for the rest of the game session.
- For personal use only. Do not distribute the imagery cache or textures built from it.
- If you don't want the risk, switch the source to `eox` (Sentinel-2 cloudless, ~10 m per pixel,
  less detailed): in `SolSatellite/PluginData/Tools/settings.ini`, section `[general]`,
  `source = eox`.

---

## How it works

**Imagery on the ground.** The plugin loads satellite imagery tiles around the camera and stacks
them into 16 nested layers: the most detailed at the camera, coarser further out. They are drawn by
a modified Parallax terrain shader, and only on Earth. Downloaded tiles stay in a disk cache, so a
repeat visit loads from disk.

**Earth from orbit (optional).** The `PluginData/Tools/solsat` utility builds a cube texture of the
whole Earth from the imagery and hooks it in with a patch. Without this step Earth looks the same
from orbit as it does in Sol.

**Buildings.** Footprints and heights come from OpenStreetMap (OpenFreeMap vector tiles, details
via the Overpass API) and from Overture Maps. Roofs are built according to the building type;
facades and roofing are textured with ambientCG materials.

**Roads and railways** are drawn over the imagery and become 3D near the camera.

**At night** street lamps light up as on the map. City lights from EnvironmentalVisualEnhancements
are visible from orbit too.

**Terrain.** Near the camera and the vessel, Sol's heights are replaced with real ones from
Mapterhorn tiles: where lidar models are published (USA, most of Europe, Japan and others) they are
detailed, elsewhere in the world the resolution is 30 m. Further from the camera Sol's terrain
remains.

**Parallax grass, trees and rocks are placed according to the map**: none on roads, runways, in
buildings or in water, and trees grow in forests.

**The mod window** opens with a button on the KSP toolbar (flight, map, space center, tracking
station). It holds the loading radii, the imagery source, the cache and the "Нагрузка" (Load) tab
with a profiler: how much of the frame each mod takes.

Downloading, map parsing and mesh building run on background threads. The disk cache is limited in
size; the oldest data is deleted automatically.

---

## Requirements

- Kerbal Space Program 1.12.5.
- Sol (tested at Quarter scale) with its dependencies: Kopernicus, Parallax Continued,
  AdvancedPQSTools, KSCSwitcher, ModuleManager.
- The mod's terrain shaders are built from Parallax Continued build `c449da1` and replace its
  shaders. With another Parallax version the terrain may render incorrectly; the shaders then need
  to be rebuilt (see "Building from source").
- Harmony (`000_Harmony`), KSPTextureLoader.
- An internet connection.

Optional:

- EnvironmentalVisualEnhancements — city lights;
- BurstPQS + KSPBurst — faster terrain building;
- Python 3 — the utility for the Earth-from-orbit texture.

⚠️ **Using it together with the Mirage mod for Sol is strongly discouraged.**

## Installation

1. Copy the `SolSatellite` folder into the game's `GameData`.
2. Start the game. Imagery, buildings and terrain load around you as you fly. The first visit to a
   new place takes time and traffic; after that everything comes from the cache.
3. Settings are changed in the mod window or in `SolSatellite/PluginData/Tools/settings.ini`.

To uninstall, delete the `GameData/SolSatellite` folder; the cache is inside it.

## Default settings

| Setting | Value |
|---|---|
| Imagery source | `google` |
| Maximum imagery zoom | 18 |
| Imagery download rate | 6 requests/s, 4 connections |
| Imagery tiles in memory | 1024 |
| Building radius | 4 km |
| Street lamp radius | 8 km |
| Road / 3D road radius | 10 km / 2.5 km |
| Terrain | on, Mapterhorn, 10 requests/s |
| Cache: imagery / maps / terrain | 20 / 5 / 5 GB |

## Earth from orbit (optional)

Run the commands in the `SolSatellite/PluginData/Tools` folder:

1. `setup.bat` — creates the Python environment.
2. `solsat.bat plan` — how many tiles are needed and how long the download will take.
3. `solsat.bat all` — download everything and build the texture. Or `live.bat`: downloads in the
   background and updates the texture right in the game.

These are thousands of requests in a row, and the risk of an IP block is highest here (see the
warning). `solsat.bat disable` / `solsat.bat enable` turn the mod off and on without deleting files.

## Building from source

- **Plugin:** you need the .NET SDK and KSP with Sol and the mods from "Requirements": the plugin is
  built against their DLLs. Point to the game folder and run the build:

  ```bat
  set "KSP_DIR=D:\Games\Kerbal Space Program"
  src\build.bat
  ```

  The DLLs go to `SolSatellite/Plugins`. You can pass your own output path:
  `src\build.bat "%KSP_DIR%\GameData\SolSatellite\Plugins\SolSatellite.dll"` (KSP must be closed
  while doing this).
- **Shaders:** you need Unity 2019.4.18f1 (the same version as KSP). `src\build_shaders.bat own`
  builds only the mod's own shaders; without the argument it also builds the Parallax terrain
  shaders. The bundles go to `SolSatellite/Shaders`.

---

## Services and data used

| Service | Used for | Rights |
|---|---|---|
| Google Maps, satellite layer | imagery (default) | © Google and imagery providers. Google's terms do not allow this use outside their apps: at your own risk only |
| EOX Sentinel-2 cloudless 2020 | imagery (`source = eox`) | Sentinel-2 cloudless — https://s2maps.eu by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2020); non-commercial use with attribution |
| OpenStreetMap | buildings, roads, railways, street lamps | © OpenStreetMap contributors, ODbL 1.0 — https://www.openstreetmap.org/copyright |
| OpenFreeMap | OpenStreetMap vector tiles | https://openfreemap.org, schema © OpenMapTiles |
| Overpass API | OpenStreetMap details | https://overpass-api.de |
| Overture Maps Foundation | building footprints | © Overture Maps Foundation, ODbL (buildings theme) |
| Mapterhorn | real terrain | https://mapterhorn.com — open elevation models: Copernicus DEM GLO-30 (© DLR e.V. 2010–2014 and © Airbus Defence and Space GmbH 2014–2018, provided under the Copernicus programme) and national lidar models; the full list of sources is on the Mapterhorn website |
| ambientCG | building and road material textures | CC0 1.0 |

## Third-party code and assets included

| What | Where | License |
|---|---|---|
| Parallax Continued (Gameslinx) — terrain and scatter shaders, modified | `src/Shaders`, `src/ShaderProject`, `SolSatellite/Shaders/ParallaxTerrain-windows.unity3d`, `TerrainScatters` in `SolSatellite/Shaders/SolSatellite-windows.unity3d` | All Rights Reserved |
| EnvironmentalVisualEnhancements (LGhassen's fork) — include files of the city lights shader | `src/Shaders/SolSatellite/EVE` | MIT |
| mapbox/earcut 2.2.4 — polygon triangulation, C# port | `src/Earcut.cs` | ISC |
| DirectXTex `texconv.exe` (Microsoft) | `SolSatellite/PluginData/Tools/bin` | MIT |

---

## License

Copyright (c) 2026 Falcon 9.

SolSat is free software. You can redistribute and modify it under the terms of the
[GNU General Public License version 3](LICENSE) (GPL-3.0) as published by the Free Software
Foundation. Modified versions must also be released under GPL-3.0, with source code and with
credit to the original.

The license covers SolSat's own code and files. The third-party parts in the table above remain
under their own licenses. Data and imagery from the services (Google, OpenStreetMap, Overture Maps,
Mapterhorn, EOX) are not part of the mod: each user downloads them themselves, under those
services' terms.

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
