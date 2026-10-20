// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Keeps the disk caches within the sizes set in [cache]: when one grows past its limit,
    /// the files used longest ago go first until it is 10% under (every read marks a file as
    /// used, see Touch). Runs on a worker thread at startup and every 10 minutes. Imagery
    /// tiles of zoom 10 and below are never deleted - the orbital texture is baked from them.
    /// </summary>
    sealed class DiskCache
    {
        public sealed class Area
        {
            public readonly string Name, Dir;
            readonly Func<string, bool> keep;
            public float LimitGb;           // 0: no limit
            public long Bytes, KeptBytes;   // total; of which never deleted
            public int Files;
            public bool Scanned;
            public DateTime ScannedAt;      // local time of the last count
            public string Result;

            public Area(string name, string dir, Func<string, bool> keep)
            {
                Name = name;
                Dir = dir;
                this.keep = keep;
            }

            public bool Keeps(string path) => path.EndsWith(".missing", StringComparison.Ordinal) || keep != null && keep(path);
        }

        const float Interval = 600f;

        public readonly Area Imagery, Osm, Terrain;
        volatile bool busy;
        float nextRun, lastStart = -1e9f;

        public bool Busy => busy;

        public DiskCache(string cacheDir)
        {
            Imagery = new Area("imagery", Path.Combine(cacheDir, "tiles"), LowZoom);
            Osm = new Area("osm", Path.Combine(cacheDir, "osm"), null);
            Terrain = new Area("terrain", Path.Combine(cacheDir, "dem"), null);
        }

        /// <summary>tiles/&lt;source&gt;/&lt;z&gt;/&lt;x&gt;/&lt;y&gt;.jpg with z at most 10.</summary>
        static bool LowZoom(string path)
        {
            string zDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
            return int.TryParse(zDir, out int z) && z <= 10;
        }

        public void Update(Settings settings)
        {
            Imagery.LimitGb = settings.ImageryCacheGb;
            Osm.LimitGb = settings.OsmCacheGb;
            Terrain.LimitGb = settings.TerrainCacheGb;
            if (!busy && Time.realtimeSinceStartup >= nextRun)
                Start(null, false);
        }

        /// <summary>Check both areas at the next Update.</summary>
        public void CheckSoon() => nextRun = 0f;

        /// <summary>The cache tab was opened: count again unless the last count is fresh. Between
        /// checks the sizes stand still, and on a new install they read 0 while the cache grows.</summary>
        public void Refresh()
        {
            if (!busy && Time.realtimeSinceStartup - lastStart > 60f)
                nextRun = 0f;
        }

        /// <summary>Deletes everything in an area that may be deleted.</summary>
        public void Clear(Area area)
        {
            if (!busy)
                Start(area, true);
        }

        void Start(Area only, bool clear)
        {
            busy = true;
            lastStart = Time.realtimeSinceStartup;
            nextRun = lastStart + Interval;
            Area[] areas = only != null ? new[] { only } : new[] { Imagery, Osm, Terrain };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (Area a in areas)
                        Trim(a, clear);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SolSatellite] Cache check failed: {e.Message}");
                }
                finally
                {
                    busy = false;
                }
            });
        }

        struct Entry
        {
            public string Path;
            public long Size;
            public DateTime Used;
        }

        static void Trim(Area a, bool clear)
        {
            var files = new List<Entry>();
            long total = 0, kept = 0;
            var root = new DirectoryInfo(a.Dir);
            if (root.Exists)
            {
                foreach (FileInfo f in root.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += f.Length;
                    if (a.Keeps(f.FullName))
                    {
                        kept += f.Length;
                        continue;
                    }
                    DateTime used = f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;
                    files.Add(new Entry { Path = f.FullName, Size = f.Length, Used = used });
                }
            }

            long limit = (long)(a.LimitGb * 1073741824.0);
            int deleted = 0;
            long freed = 0;
            if (clear || a.LimitGb > 0f && total > limit)
            {
                long target = clear ? 0 : (long)(limit * 0.9);
                files.Sort((x, y) => x.Used.CompareTo(y.Used)); // used longest ago first
                foreach (Entry e in files)
                {
                    if (total <= target)
                        break;
                    try
                    {
                        File.Delete(e.Path);
                        total -= e.Size;
                        freed += e.Size;
                        deleted++;
                    }
                    catch (IOException) { }                 // being read or written right now
                    catch (UnauthorizedAccessException) { }
                }
            }

            a.Bytes = total;
            a.KeptBytes = kept;
            a.Files = files.Count - deleted;
            a.ScannedAt = DateTime.Now;
            a.Scanned = true;
            if (deleted > 0)
            {
                a.Result = $"удалено {deleted} файлов, {freed / 1048576.0:0} МБ";
                Debug.Log($"[SolSatellite] Cache '{a.Name}': deleted {deleted} files, {freed / 1048576.0:0} MB");
            }
        }

        /// <summary>Marks a cache file as just used, so it is kept longest.</summary>
        public static void Touch(string path)
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception) { }
        }
    }
}
