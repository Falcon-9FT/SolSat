// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace SolSatellite
{
    /// <summary>
    /// Loads web-map tiles as textures: from the shared disk cache when present (the same
    /// Cache/tiles/&lt;source&gt;/z/x/y.jpg layout the solsat tool writes), otherwise from the tile
    /// server - rate limited, and saved to the cache afterwards. Tiles asked for most recently
    /// are served first. Tiles the clipmap no longer covers (flown away from) are dropped from
    /// the queue and their downloads cancelled, and so are ones nobody has asked for in a while.
    /// </summary>
    sealed class TileStreamer : IDisposable
    {
        enum State { Probing, Queued, Loading, Ready, Missing, Failed }

        sealed class Tile
        {
            public long Key;
            public int Z, X, Y;
            public State State;
            public Texture2D Texture;
            public int LastWanted;
            public float WantedAt;    // realtime of LastWanted
            public bool Download;     // may come from the network (not just from the disk cache)
            public int Attempts;
            public string CachedFile; // null when not on disk
            public UnityWebRequest Request; // while downloading
            public bool Dropped;      // no longer needed while downloading: the reply is thrown away
        }

        const int MaxQueued = 512;
        const int MaxDiskLoads = 8;
        const int ProbeBatch = 256;      // disk cache look-ups per batch, on a worker thread
        const string MissingMark = "";   // a probe's answer: the server has no such tile
        const int StaleFrames = 300;
        const int MaxStrikes = 4;
        const float IdleSeconds = 30f; // a loaded texture the clipmap hasn't asked for this long is let go

        readonly Settings settings;
        readonly string cacheRoot;
        readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        readonly List<Tile> queue = new List<Tile>();
        readonly List<long> prefetch = new List<long>(); // download-only, when nothing else needs the network
        readonly List<Tile> gone = new List<Tile>();
        // New tiles are looked up in the disk cache off the main thread (up to four File.Exists
        // each: with fine levels moving every few metres, dozens of new tiles a frame).
        readonly List<Tile> probeQueue = new List<Tile>();
        Tile[] probing;
        string[] probeFound;
        volatile bool probeDone;
        int diskLoads, netLoads, residentCount;
        float netTokens;
        float pausedUntil;
        int strikes;
        bool networkDisabled, disposed;
        float nextIdleCheck;

        public int Downloaded { get; private set; }
        public int PrefetchTotal { get; private set; }
        public int PrefetchDone { get; private set; }
        /// <summary>Set from the settings window; stops new downloads (disk loads continue).</summary>
        public bool Paused;

        public TileStreamer(Settings settings, string tilesDir)
        {
            this.settings = settings;
            cacheRoot = Path.Combine(tilesDir, settings.SourceName);
        }

        public int Queued => queue.Count;
        public int InFlight => netLoads + diskLoads;
        public int Resident => residentCount;
        /// <summary>Tile textures held at most, below MemoryTiles while the card is short of memory.</summary>
        public int ResidentCap = int.MaxValue;
        /// <summary>Video memory the loaded tile textures take, roughly (4 bytes a pixel, plus mips).</summary>
        public long ResidentBytes { get; private set; }

        static long Bytes(Texture2D t) => t == null ? 0 : (long)t.width * t.height * 4 * (t.mipmapCount > 1 ? 4 : 3) / 3;

        public string NetworkStatus
        {
            get
            {
                if (networkDisabled)
                    return "отключена: сервер отказывает (до перезапуска игры)";
                float wait = pausedUntil - Time.realtimeSinceStartup;
                if (wait > 0f)
                    return $"сервер отказал, повтор через {wait:F0} с";
                return Paused ? "пауза" : "в норме";
            }
        }

        /// <summary>Queues tiles to be downloaded into the disk cache in the background.</summary>
        public void Prefetch(List<long> keys)
        {
            prefetch.Clear();
            prefetch.AddRange(keys);
            prefetch.Reverse(); // served from the end
            PrefetchTotal = keys.Count;
            PrefetchDone = 0;
        }

        public void CancelPrefetch()
        {
            prefetch.Clear();
            PrefetchTotal = PrefetchDone = 0;
        }

        public bool Prefetching => prefetch.Count > 0;

        /// <summary>The tile's texture if loaded; otherwise requests it and returns null.
        /// Without download it is only loaded if it is in the disk cache (the camera moves too
        /// fast for a download to arrive in time). missing is set when the server has no such tile.</summary>
        public Texture2D Get(int z, int x, int y, bool download, out bool missing)
        {
            missing = false;
            long key = Geo.TileKey(z, x, y);
            if (!tiles.TryGetValue(key, out Tile t))
            {
                // Looked up in the disk cache first (UpdateProbes), then queued.
                t = new Tile { Key = key, Z = z, X = x, Y = y, State = State.Probing };
                tiles[key] = t;
                probeQueue.Add(t);
            }
            // Asked for by several levels in one frame: any of them may have it downloaded.
            if (t.LastWanted != Time.frameCount)
                t.Download = false;
            t.Download |= download;
            t.LastWanted = Time.frameCount;
            t.WantedAt = Time.realtimeSinceStartup;
            if (t.State == State.Failed && t.Attempts < 3)
            {
                t.State = State.Queued;
                queue.Add(t);
            }
            missing = t.State == State.Missing;
            return t.State == State.Ready ? t.Texture : null;
        }

        public void Update()
        {
            float rate = Mathf.Max(settings.StreamRequestsPerSecond, 0.1f);
            netTokens = Mathf.Min(netTokens + Time.unscaledDeltaTime * rate, Mathf.Max(rate, 1f));
            bool networkOpen = !Paused && !networkDisabled && Time.realtimeSinceStartup >= pausedUntil;
            UpdateProbes();
            UpdateQueue(networkOpen);
            UpdatePrefetch(networkOpen);
            EvictUnused();
        }

        /// <summary>Takes in the last batch of disk cache look-ups and starts the next: the most
        /// recently wanted tiles first; ones nobody wants any more are forgotten.</summary>
        void UpdateProbes()
        {
            if (probing != null)
            {
                if (!probeDone)
                    return;
                for (int i = 0; i < probing.Length; i++)
                {
                    Tile t = probing[i];
                    if (t.State != State.Probing || !tiles.TryGetValue(t.Key, out Tile now) || now != t)
                        continue; // dropped meanwhile
                    if (probeFound[i] == MissingMark)
                    {
                        t.State = State.Missing;
                    }
                    else
                    {
                        t.CachedFile = probeFound[i];
                        t.State = State.Queued;
                        queue.Add(t);
                    }
                }
                probing = null;
                probeFound = null;
            }
            if (probeQueue.Count == 0)
                return;
            int frame = Time.frameCount;
            probeQueue.RemoveAll(t =>
            {
                if (t.State != State.Probing || !tiles.TryGetValue(t.Key, out Tile now) || now != t)
                    return true; // dropped meanwhile
                if (frame - t.LastWanted <= StaleFrames)
                    return false;
                tiles.Remove(t.Key); // not wanted for a while
                return true;
            });
            if (probeQueue.Count == 0)
                return;
            probeQueue.Sort((a, b) => b.LastWanted.CompareTo(a.LastWanted));
            int n = Math.Min(probeQueue.Count, ProbeBatch);
            Tile[] batch = probing = probeQueue.GetRange(0, n).ToArray();
            probeQueue.RemoveRange(0, n);
            string[] found = probeFound = new string[n];
            probeDone = false;
            string root = cacheRoot;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < batch.Length; i++)
                {
                    try
                    {
                        Tile t = batch[i];
                        string baseName = Path.Combine(root, t.Z.ToString(), t.X.ToString(), t.Y.ToString());
                        found[i] = File.Exists(baseName + ".missing") ? MissingMark
                                 : FirstExisting(baseName + ".jpg", baseName + ".png", baseName + ".webp");
                    }
                    catch (Exception)
                    {
                        found[i] = null; // download it
                    }
                }
                probeDone = true;
            });
        }

        void UpdateQueue(bool networkOpen)
        {
            if (queue.Count == 0)
                return;
            int now = Time.frameCount;
            // Wanted most recently first; drop what nobody has asked for lately.
            queue.RemoveAll(t =>
            {
                bool stale = now - t.LastWanted > StaleFrames;
                if (stale)
                    tiles.Remove(t.Key);
                return stale;
            });
            queue.Sort((a, b) => b.LastWanted.CompareTo(a.LastWanted));
            if (queue.Count > MaxQueued)
            {
                for (int i = MaxQueued; i < queue.Count; i++)
                    tiles.Remove(queue[i].Key);
                queue.RemoveRange(MaxQueued, queue.Count - MaxQueued);
            }

            for (int i = 0; i < queue.Count;)
            {
                Tile t = queue[i];
                if (t.CachedFile != null)
                {
                    if (diskLoads < MaxDiskLoads)
                    {
                        queue.RemoveAt(i);
                        StartLoad(t, new Uri(t.CachedFile).AbsoluteUri, false);
                        continue;
                    }
                }
                else if (t.Download && networkOpen && netLoads < settings.StreamConnections && netTokens >= 1f)
                {
                    netTokens -= 1f;
                    queue.RemoveAt(i);
                    StartLoad(t, settings.TileUrl(t.Z, t.X, t.Y), true);
                    continue;
                }
                i++;
            }
        }

        /// <summary>The clipmap says which tiles its windows cover now. Anything else still
        /// waiting is dropped and its download cancelled: flown away from, it isn't needed.
        /// Loaded textures stay (until memory runs short), and so does the area download.</summary>
        public void Retain(HashSet<long> needed)
        {
            queue.RemoveAll(t =>
            {
                bool drop = !needed.Contains(t.Key);
                if (drop)
                    tiles.Remove(t.Key);
                return drop;
            });
            gone.Clear();
            foreach (Tile t in tiles.Values)
                if (!needed.Contains(t.Key) && (t.State == State.Failed || t.State == State.Probing || t.State == State.Loading && t.Request != null))
                    gone.Add(t);
            foreach (Tile t in gone)
            {
                tiles.Remove(t.Key);
                if (t.State != State.Loading)
                    continue;
                // Its connection is free right away; Finish throws the reply away.
                t.Dropped = true;
                netLoads--;
                Cancelled++;
                t.Request.Abort();
            }
            gone.Clear();
        }

        /// <summary>Downloads cancelled because the tile was no longer needed.</summary>
        public int Cancelled { get; private set; }

        void UpdatePrefetch(bool networkOpen)
        {
            while (prefetch.Count > 0 && networkOpen && netLoads < settings.StreamConnections && netTokens >= 1f)
            {
                long key = prefetch[prefetch.Count - 1];
                prefetch.RemoveAt(prefetch.Count - 1);
                int z = (int)(key >> 42), y = (int)((key >> 21) & 0x1FFFFF), x = (int)(key & 0x1FFFFF);
                string baseName = Path.Combine(cacheRoot, z.ToString(), x.ToString(), y.ToString());
                if (tiles.ContainsKey(key) || FirstExisting(baseName + ".jpg", baseName + ".png", baseName + ".webp", baseName + ".missing") != null)
                {
                    PrefetchDone++;
                    continue;
                }
                netTokens -= 1f;
                netLoads++;
                UnityWebRequest req = UnityWebRequest.Get(settings.TileUrl(z, x, y));
                req.timeout = 30;
                req.SetRequestHeader("User-Agent", settings.UserAgent);
                var tile = new Tile { Key = key, Z = z, X = x, Y = y };
                req.SendWebRequest().completed += _ => FinishPrefetch(tile, req);
            }
        }

        void FinishPrefetch(Tile t, UnityWebRequest req)
        {
            netLoads--;
            try
            {
                if (disposed)
                    return;
                long code = req.responseCode;
                if (code == 404 || code == 204)
                {
                    MarkMissing(t);
                    PrefetchDone++;
                    return;
                }
                string ext = req.isNetworkError || req.isHttpError ? null : ImageExtension(req.downloadHandler.data);
                if (ext != null)
                {
                    strikes = 0;
                    Downloaded++;
                    PrefetchDone++;
                    Save(t, req.downloadHandler.data, ext);
                    return;
                }
                if (code == 403 || code == 429 || (code == 200 && !req.isNetworkError))
                {
                    Refused(code == 200 ? "non-image reply" : $"HTTP {code}");
                    prefetch.Insert(0, t.Key); // retried after the pause
                }
                else
                {
                    PrefetchDone++; // other failures: skip; the streamer fetches it if it is ever shown
                }
            }
            finally
            {
                req.Dispose();
            }
        }

        void StartLoad(Tile t, string uri, bool fromNetwork)
        {
            t.State = State.Loading;
            if (fromNetwork)
                netLoads++;
            else
                diskLoads++;
            UnityWebRequest req = UnityWebRequestTexture.GetTexture(uri, true);
            req.timeout = 30;
            if (fromNetwork)
            {
                req.SetRequestHeader("User-Agent", settings.UserAgent);
                t.Request = req;
            }
            req.SendWebRequest().completed += _ => Finish(t, req, fromNetwork);
        }

        void Finish(Tile t, UnityWebRequest req, bool fromNetwork)
        {
            t.Request = null;
            if (t.Dropped)
            {
                req.Dispose(); // cancelled by Retain, which has let go of its connection already
                return;
            }
            if (fromNetwork)
                netLoads--;
            else
                diskLoads--;
            Perf.Begin(Perf.Part.TileLoad);
            try
            {
                if (disposed)
                    return;
                if (!fromNetwork)
                {
                    string file = t.CachedFile;
                    if (req.isNetworkError || req.isHttpError || ImageExtension(req.downloadHandler.data) == null)
                    {
                        // Gone from the cache (trimmed to its size limit) or damaged: download it again.
                        try
                        {
                            if (file != null && File.Exists(file))
                                File.Delete(file);
                        }
                        catch (Exception) { }
                        t.CachedFile = null;
                        Requeue(t);
                        return;
                    }
                    ThreadPool.QueueUserWorkItem(_ => DiskCache.Touch(file)); // used: kept longest
                }
                long code = req.responseCode;
                if (req.isNetworkError)
                {
                    Retry(t);
                    return;
                }
                if (fromNetwork && (code == 404 || code == 204))
                {
                    MarkMissing(t);
                    return;
                }
                if (fromNetwork && (code == 403 || code == 429))
                {
                    Refused($"HTTP {code}");
                    Requeue(t);
                    return;
                }
                if (req.isHttpError)
                {
                    Retry(t);
                    return;
                }
                byte[] data = req.downloadHandler.data;
                string ext = ImageExtension(data);
                if (ext == null)
                {
                    // An HTML page instead of an image usually means "unusual traffic".
                    if (fromNetwork)
                    {
                        Refused("non-image reply");
                        Requeue(t);
                    }
                    else
                    {
                        t.State = State.Failed;
                        t.Attempts = 99;
                    }
                    return;
                }
                Texture2D tex = DownloadHandlerTexture.GetContent(req);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                t.Texture = tex;
                t.State = State.Ready;
                residentCount++;
                ResidentBytes += Bytes(tex);
                if (fromNetwork)
                {
                    strikes = 0;
                    Downloaded++;
                    Save(t, data, ext);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                t.State = State.Failed;
            }
            finally
            {
                req.Dispose();
                Perf.End(Perf.Part.TileLoad);
            }
        }

        void Retry(Tile t)
        {
            t.Attempts++;
            t.State = State.Failed; // Get() requeues it while it is still wanted, up to 3 attempts
        }

        void Requeue(Tile t)
        {
            t.State = State.Queued;
            queue.Add(t);
        }

        void Refused(string why)
        {
            if (Time.realtimeSinceStartup < pausedUntil)
                return;
            strikes++;
            if (strikes > MaxStrikes)
            {
                networkDisabled = true;
                Debug.LogWarning($"[SolSatellite] Tile server keeps refusing ({why}); downloads off for this session");
                return;
            }
            float pause = Mathf.Min(900f, 60f * (1 << (strikes - 1)));
            pausedUntil = Time.realtimeSinceStartup + pause;
            Debug.LogWarning($"[SolSatellite] Tile server refused ({why}); pausing downloads for {pause:F0} s");
        }

        void MarkMissing(Tile t)
        {
            t.State = State.Missing;
            string file = Path.Combine(cacheRoot, t.Z.ToString(), t.X.ToString(), t.Y + ".missing");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, new byte[0]);
                }
                catch (Exception) { }
            });
        }

        void Save(Tile t, byte[] data, string ext)
        {
            string file = Path.Combine(cacheRoot, t.Z.ToString(), t.X.ToString(), t.Y + ext);
            t.CachedFile = file;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    string tmp = file + ".part" + Thread.CurrentThread.ManagedThreadId;
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(file))
                        File.Delete(tmp);
                    else
                        File.Move(tmp, file);
                }
                catch (Exception) { }
            });
        }

        void EvictUnused()
        {
            // Textures are only needed to draw tiles into the clipmap; one not asked for in a
            // while (drawn and left behind) goes, and comes back from the disk cache if needed.
            float now = Time.realtimeSinceStartup;
            if (now >= nextIdleCheck)
            {
                nextIdleCheck = now + 2f;
                var idle = new List<Tile>();
                foreach (Tile t in tiles.Values)
                    if (t.State == State.Ready && now - t.WantedAt > IdleSeconds)
                        idle.Add(t);
                foreach (Tile t in idle)
                {
                    ResidentBytes -= Bytes(t.Texture);
                    UnityEngine.Object.Destroy(t.Texture);
                    tiles.Remove(t.Key);
                    residentCount--;
                }
            }
            int maxResident = Math.Min(settings.MemoryTiles, ResidentCap);
            if (residentCount <= maxResident + maxResident / 8)
                return;
            var resident = new List<Tile>();
            foreach (Tile t in tiles.Values)
                if (t.State == State.Ready)
                    resident.Add(t);
            resident.Sort((a, b) => a.LastWanted.CompareTo(b.LastWanted));
            for (int i = 0; i < resident.Count - maxResident; i++)
            {
                Tile t = resident[i];
                ResidentBytes -= Bytes(t.Texture);
                UnityEngine.Object.Destroy(t.Texture);
                tiles.Remove(t.Key);
            }
            residentCount = Math.Min(resident.Count, maxResident);
        }

        /// <summary>Frees every loaded tile texture (they reload from disk when needed).</summary>
        public void ReleaseTextures()
        {
            var keep = new List<Tile>();
            foreach (Tile t in tiles.Values)
            {
                if (t.State == State.Ready)
                    UnityEngine.Object.Destroy(t.Texture);
                else if (t.State == State.Loading || t.State == State.Missing)
                    keep.Add(t);
            }
            tiles.Clear();
            foreach (Tile t in keep)
                tiles[t.Key] = t;
            queue.Clear();
            residentCount = 0;
            ResidentBytes = 0;
        }

        static string FirstExisting(params string[] files)
        {
            foreach (string f in files)
                if (File.Exists(f))
                    return f;
            return null;
        }

        static string ImageExtension(byte[] data)
        {
            if (data == null || data.Length < 12)
                return null;
            if (data[0] == 0xFF && data[1] == 0xD8)
                return ".jpg";
            if (data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
                return ".png";
            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[8] == (byte)'W' && data[9] == (byte)'E')
                return ".webp";
            return null;
        }

        public void Dispose()
        {
            disposed = true;
            foreach (Tile t in tiles.Values)
            {
                if (t.Texture != null)
                    UnityEngine.Object.Destroy(t.Texture);
                t.Request?.Abort();
            }
            tiles.Clear();
            queue.Clear();
        }
    }
}
