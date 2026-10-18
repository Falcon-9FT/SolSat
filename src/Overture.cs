// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace SolSatellite
{
    /// <summary>A z14 tile asked of OvertureStore. Done once Data, Missing or Error is set.</summary>
    sealed class OvertureRequest
    {
        public readonly int X, Y;
        public bool Done;
        public byte[] Data;     // the tile as stored (a gzipped vector tile)
        public bool Missing;    // the archive has no such tile: Overture knows no buildings there
        public string Error;
        internal readonly long TileId;
        internal bool Cancelled;
        internal UnityWebRequest Web;
        internal RangeDownload Download;

        public OvertureRequest(int x, int y, long id)
        {
            X = x;
            Y = y;
            TileId = id;
        }

        /// <summary>Not wanted any more: dropped, its download aborted.</summary>
        public void Cancel() => Cancelled = true;
    }

    /// <summary>Takes exactly the bytes of a range request, and aborts a reply that brings
    /// more: a server ignoring the range would send the whole 180 GB archive.</summary>
    sealed class RangeDownload : DownloadHandlerScript
    {
        readonly byte[] buffer;
        int received;

        public RangeDownload(int length) : base(new byte[65536])
        {
            buffer = new byte[length];
        }

        public bool Complete => received == buffer.Length;
        public bool TooMuch { get; private set; }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0)
                return true;
            if (received + dataLength > buffer.Length)
            {
                TooMuch = true;
                return false;
            }
            Buffer.BlockCopy(data, 0, buffer, received, dataLength);
            received += dataLength;
            return true;
        }

        protected override byte[] GetData() => buffer;
    }

    /// <summary>
    /// Overture Maps' buildings, read straight from the PMTiles archive the Overture Maps
    /// Foundation publishes on Amazon S3 for every monthly release (open data, ODbL), with HTTP
    /// range requests: the archive's header and root directory once, a leaf directory now and
    /// then (both cached on disk), then one request per z14 tile. Releases are kept for 60 days
    /// and the newest is found by listing the bucket - again whenever the one in use is gone.
    /// Tiles themselves aren't kept here: BuildingManager merges them into its OSM cache.
    /// </summary>
    sealed class OvertureStore : IDisposable
    {
        const string UserAgent = "SolSatellite/0.3 (personal KSP mod)";
        const int HeaderBytes = 16384;          // header and root directory are in the first 16 KB
        const double ReleaseDays = 1.0;         // look for a newer release after this long
        const int Zoom = OsmTile.Zoom;

        struct Entry
        {
            public long TileId, Offset;
            public int Length, RunLength; // run length 0: a leaf directory
        }

        enum State { Idle, Listing, Header, Ready }

        sealed class LeafFetch
        {
            public UnityWebRequest Web;
            public RangeDownload Download;
            public string Path, Release;
        }

        readonly string baseUrl, cacheRoot;
        readonly List<OvertureRequest> queue = new List<OvertureRequest>();
        readonly Dictionary<long, Entry[]> leaves = new Dictionary<long, Entry[]>();
        readonly Dictionary<long, LeafFetch> leafFetches = new Dictionary<long, LeafFetch>();
        State state;
        UnityWebRequest stateRequest;
        RangeDownload stateDownload;
        List<string> releases;
        int releaseIndex, failures, errorsInRow;
        float retryAt, pauseUntil;
        bool relist;
        string release, archiveUrl;
        long leafOffset, dataOffset;
        byte internalCompression;
        Entry[] root;
        int active;

        public int MaxRequests = 4;
        public int Downloaded { get; private set; }
        public int MissingTiles { get; private set; }
        public long DownloadedBytes { get; private set; }
        public string Problem { get; private set; }
        public string Release => state == State.Ready ? release : null;
        public int Pending => queue.Count;

        /// <summary>Usable or getting there; false while waiting to try again after failures.</summary>
        public bool Available => state == State.Ready || failures < 3;

        public OvertureStore(string baseUrl, string cacheRoot)
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.cacheRoot = cacheRoot;
        }

        public OvertureRequest Want(int x, int y)
        {
            var r = new OvertureRequest(x, y, TileId(Zoom, x, y));
            queue.Add(r);
            return r;
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (state != State.Ready)
                Connect(now);
            PollLeaves(now);
            for (int i = 0; i < queue.Count; i++)
            {
                OvertureRequest r = queue[i];
                if (r.Cancelled || Step(r, now))
                {
                    if (r.Web != null)
                    {
                        if (!r.Web.isDone)
                            r.Web.Abort();
                        r.Web.Dispose();
                        r.Web = null;
                        active--;
                    }
                    queue.RemoveAt(i--);
                }
            }
        }

        /// <summary>Moves a request on; true once it is done.</summary>
        bool Step(OvertureRequest r, float now)
        {
            if (r.Web != null)
            {
                if (!r.Web.isDone)
                    return false;
                Finish(r, now);
                return r.Done;
            }
            if (state != State.Ready || active >= MaxRequests || now < pauseUntil)
                return false;
            Entry[] dir = root;
            for (int depth = 0; depth < 4; depth++)
            {
                if (!Find(dir, r.TileId, out Entry e))
                    break;
                if (e.RunLength == 0)
                {
                    long at = leafOffset + e.Offset;
                    if (!leaves.TryGetValue(at, out dir))
                    {
                        if (!leafFetches.ContainsKey(at) && active < MaxRequests)
                            FetchLeaf(at, e.Length);
                        return false;
                    }
                    continue;
                }
                if (r.TileId >= e.TileId + e.RunLength)
                    break;
                r.Download = new RangeDownload(e.Length);
                r.Web = RangeRequest(dataOffset + e.Offset, e.Length, r.Download);
                active++;
                return false;
            }
            r.Missing = r.Done = true;
            MissingTiles++;
            return true;
        }

        void Finish(OvertureRequest r, float now)
        {
            UnityWebRequest web = r.Web;
            long code = web.responseCode;
            if (!web.isNetworkError && code == 206 && r.Download.Complete)
            {
                r.Data = r.Download.data;
                r.Done = true;
                Downloaded++;
                DownloadedBytes += r.Data.Length;
                Problem = null;
                errorsInRow = 0;
            }
            else if (code == 403 || code == 404)
            {
                Gone(); // the release was taken down: find the current one, then try again
            }
            else
            {
                r.Error = web.isNetworkError ? web.error : r.Download.TooMuch ? "сервер не понял запрос диапазона" : $"HTTP {code}";
                r.Done = true;
                Error(now, r.Error);
            }
            web.Dispose();
            r.Web = null;
            r.Download = null;
            active--;
        }

        /// <summary>A failed request: new ones wait a little, longer the more fail in a row.</summary>
        void Error(float now, string why)
        {
            Problem = why;
            errorsInRow++;
            pauseUntil = now + Math.Min(60f, 2f * errorsInRow);
        }

        // ------------------------------------------------------------------ release and header

        void Connect(float now)
        {
            if (stateRequest != null)
            {
                if (!stateRequest.isDone)
                    return;
                if (state == State.Listing)
                    FinishListing(now);
                else
                    FinishHeader(now);
                return;
            }
            if (now < retryAt)
                return;
            if (!relist && TryCachedRelease())
                return;
            relist = false;
            state = State.Listing;
            stateRequest = UnityWebRequest.Get(baseUrl + "/?list-type=2&prefix=tiles/&delimiter=/");
            stateRequest.timeout = 30;
            stateRequest.SetRequestHeader("User-Agent", UserAgent);
            stateRequest.SendWebRequest();
        }

        static readonly Regex ReleasePrefix = new Regex(@"<Prefix>tiles/([0-9][^<>/]*)/</Prefix>");

        void FinishListing(float now)
        {
            UnityWebRequest req = stateRequest;
            stateRequest = null;
            try
            {
                if (req.isNetworkError || req.responseCode != 200)
                {
                    Fail(now, req.isNetworkError ? "нет связи: " + req.error : $"список релизов: HTTP {req.responseCode}");
                    return;
                }
                releases = new List<string>();
                foreach (Match m in ReleasePrefix.Matches(req.downloadHandler.text ?? ""))
                    releases.Add(m.Groups[1].Value);
                releases.Sort(StringComparer.Ordinal);
                releases.Reverse(); // newest first ("2026-09-23.1")
                if (releases.Count == 0)
                {
                    Fail(now, "в хранилище нет релизов");
                    return;
                }
                releaseIndex = 0;
                StartHeader();
            }
            finally
            {
                req.Dispose();
            }
        }

        void StartHeader()
        {
            state = State.Header;
            release = releases[releaseIndex];
            archiveUrl = $"{baseUrl}/tiles/{release}/buildings.pmtiles";
            stateDownload = new RangeDownload(HeaderBytes);
            stateRequest = RangeRequest(0, HeaderBytes, stateDownload);
        }

        void FinishHeader(float now)
        {
            UnityWebRequest req = stateRequest;
            stateRequest = null;
            try
            {
                long code = req.responseCode;
                if ((code == 403 || code == 404) && ++releaseIndex < releases.Count)
                {
                    StartHeader(); // this one has no buildings archive (yet): the one before
                    return;
                }
                if (req.isNetworkError || code != 206 || !stateDownload.Complete)
                {
                    Fail(now, req.isNetworkError ? "нет связи: " + req.error : $"заголовок архива: HTTP {code}");
                    return;
                }
                byte[] head = stateDownload.data;
                ReadHeader(head);
                string dir = Path.Combine(cacheRoot, release);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "header.bin"), head);
                File.WriteAllText(Path.Combine(cacheRoot, "release.txt"),
                                  release + "\n" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (string other in Directory.GetDirectories(cacheRoot))
                    if (Path.GetFileName(other) != release)
                        TryDelete(other); // leaf directories of releases gone by
                Ready();
                Debug.Log($"[SolSatellite] Overture buildings: release {release}");
            }
            catch (Exception e)
            {
                Fail(now, e.Message);
            }
            finally
            {
                req.Dispose();
                stateDownload = null;
            }
        }

        /// <summary>The release found less than a day ago, with its header on disk: no request.</summary>
        bool TryCachedRelease()
        {
            try
            {
                string[] lines = File.ReadAllLines(Path.Combine(cacheRoot, "release.txt"));
                if (lines.Length < 2 || !long.TryParse(lines[1], out long ticks) ||
                    (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalDays > ReleaseDays)
                    return false;
                string header = Path.Combine(cacheRoot, lines[0], "header.bin");
                if (!File.Exists(header))
                    return false;
                release = lines[0];
                archiveUrl = $"{baseUrl}/tiles/{release}/buildings.pmtiles";
                ReadHeader(File.ReadAllBytes(header));
                Ready();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void Ready()
        {
            state = State.Ready;
            failures = 0;
            Problem = null;
            leaves.Clear();
        }

        void Fail(float now, string why)
        {
            state = State.Idle;
            failures++;
            retryAt = now + Math.Min(600f, 30f * failures);
            Problem = why;
            Debug.LogWarning($"[SolSatellite] Overture buildings: {why}");
        }

        /// <summary>The archive in use is gone (a new release replaced it).</summary>
        void Gone()
        {
            if (state != State.Ready)
                return;
            state = State.Idle;
            relist = true;
            retryAt = 0f;
            try
            {
                File.Delete(Path.Combine(cacheRoot, "release.txt"));
            }
            catch (Exception) { }
            Debug.Log($"[SolSatellite] Overture buildings: release {release} is gone, looking for the current one");
        }

        /// <summary>PMTiles v3 header: offsets of the root directory, the leaf directories and
        /// the tile data; z14 has to be among its zoom levels.</summary>
        void ReadHeader(byte[] h)
        {
            if (h.Length < 127 || Encoding.ASCII.GetString(h, 0, 7) != "PMTiles" || h[7] != 3)
                throw new InvalidDataException("не архив PMTiles v3");
            long rootOffset = BitConverter.ToInt64(h, 8), rootLength = BitConverter.ToInt64(h, 16);
            leafOffset = BitConverter.ToInt64(h, 40);
            dataOffset = BitConverter.ToInt64(h, 56);
            byte internalCompression = h[97], tileCompression = h[98], minZoom = h[100], maxZoom = h[101];
            if (internalCompression > 2 || tileCompression > 2)
                throw new InvalidDataException($"сжатие {internalCompression}/{tileCompression} не поддерживается");
            if (Zoom < minZoom || Zoom > maxZoom)
                throw new InvalidDataException($"в архиве уровни {minZoom}-{maxZoom}, нужен {Zoom}");
            if (rootOffset + rootLength > h.Length)
                throw new InvalidDataException("корневой каталог не в начале архива");
            root = ReadDirectory(Decompress(h, (int)rootOffset, (int)rootLength, internalCompression));
            this.internalCompression = internalCompression;
        }

        // ------------------------------------------------------------------ leaf directories

        void FetchLeaf(long at, int length)
        {
            string path = Path.Combine(cacheRoot, release, $"leaf_{at}_{length}.bin");
            if (File.Exists(path))
            {
                try
                {
                    leaves[at] = ReadDirectory(Decompress(File.ReadAllBytes(path), 0, length, internalCompression));
                    return;
                }
                catch (Exception)
                {
                    TryDelete(path);
                }
            }
            var download = new RangeDownload(length);
            leafFetches[at] = new LeafFetch { Web = RangeRequest(at, length, download), Download = download, Path = path, Release = release };
            active++;
        }

        void PollLeaves(float now)
        {
            if (leafFetches.Count == 0)
                return;
            List<long> done = null;
            foreach (var kv in leafFetches)
            {
                LeafFetch f = kv.Value;
                if (!f.Web.isDone)
                    continue;
                (done ??= new List<long>()).Add(kv.Key);
                try
                {
                    long code = f.Web.responseCode;
                    if (f.Release != release || state != State.Ready)
                    {
                        // fetched for a release no longer in use
                    }
                    else if (!f.Web.isNetworkError && code == 206 && f.Download.Complete)
                    {
                        byte[] raw = f.Download.data;
                        leaves[kv.Key] = ReadDirectory(Decompress(raw, 0, raw.Length, internalCompression));
                        DownloadedBytes += raw.Length;
                        errorsInRow = 0;
                        Directory.CreateDirectory(Path.GetDirectoryName(f.Path));
                        File.WriteAllBytes(f.Path, raw);
                    }
                    else if (code == 403 || code == 404)
                    {
                        Gone();
                    }
                    else
                    {
                        Error(now, f.Web.isNetworkError ? "нет связи: " + f.Web.error : $"каталог: HTTP {code}");
                    }
                }
                catch (Exception e)
                {
                    Error(now, e.Message);
                }
                f.Web.Dispose();
                active--;
            }
            if (done != null)
                foreach (long k in done)
                    leafFetches.Remove(k);
        }

        // ------------------------------------------------------------------ PMTiles

        UnityWebRequest RangeRequest(long offset, int length, RangeDownload download)
        {
            var req = new UnityWebRequest(archiveUrl, UnityWebRequest.kHttpVerbGET) { downloadHandler = download, timeout = 30 };
            req.SetRequestHeader("User-Agent", UserAgent);
            req.SetRequestHeader("Range", $"bytes={offset}-{offset + length - 1}");
            req.SendWebRequest();
            return req;
        }

        static byte[] Decompress(byte[] data, int offset, int length, byte compression)
        {
            if (compression != 2)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, offset, copy, 0, length);
                return copy;
            }
            using (var input = new GZipStream(new MemoryStream(data, offset, length), CompressionMode.Decompress))
            using (var output = new MemoryStream(length * 4))
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>A directory: entry count, then tile ids (deltas), run lengths, lengths and
        /// offsets (0: right after the previous entry, else offset + 1), each a varint column.</summary>
        static Entry[] ReadDirectory(byte[] b)
        {
            int p = 0;
            long n = Varint(b, ref p);
            if (n < 0 || n > 10000000)
                throw new InvalidDataException("каталог повреждён");
            var entries = new Entry[n];
            long id = 0;
            for (int i = 0; i < n; i++)
            {
                id += Varint(b, ref p);
                entries[i].TileId = id;
            }
            for (int i = 0; i < n; i++)
                entries[i].RunLength = (int)Varint(b, ref p);
            for (int i = 0; i < n; i++)
                entries[i].Length = (int)Varint(b, ref p);
            for (int i = 0; i < n; i++)
            {
                long v = Varint(b, ref p);
                entries[i].Offset = v == 0 && i > 0 ? entries[i - 1].Offset + entries[i - 1].Length : v - 1;
            }
            return entries;
        }

        static long Varint(byte[] b, ref int p)
        {
            long result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (p >= b.Length)
                    throw new InvalidDataException("каталог обрезан");
                byte c = b[p++];
                result |= (long)(c & 0x7F) << shift;
                if (c < 0x80)
                    return result;
            }
            throw new InvalidDataException("каталог повреждён");
        }

        /// <summary>The entry with the greatest tile id not above id.</summary>
        static bool Find(Entry[] dir, long id, out Entry found)
        {
            int lo = 0, hi = dir.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (dir[mid].TileId <= id)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            found = best >= 0 ? dir[best] : default;
            return best >= 0;
        }

        /// <summary>PMTiles tile id: tiles of all lower zooms first, then the position along
        /// the Hilbert curve at this zoom.</summary>
        public static long TileId(int z, int x, int y)
        {
            long acc = ((1L << (2 * z)) - 1) / 3;
            long n = 1L << z, d = 0, tx = x, ty = y;
            for (long s = n >> 1; s > 0; s >>= 1)
            {
                long rx = (tx & s) > 0 ? 1 : 0, ry = (ty & s) > 0 ? 1 : 0;
                d += s * s * ((3 * rx) ^ ry);
                if (ry == 0)
                {
                    if (rx == 1)
                    {
                        tx = n - 1 - tx;
                        ty = n - 1 - ty;
                    }
                    long t = tx;
                    tx = ty;
                    ty = t;
                }
            }
            return acc + d;
        }

        static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception) { }
        }

        public string Status
        {
            get
            {
                switch (state)
                {
                    case State.Listing:
                        return "ищем свежий релиз";
                    case State.Header:
                        return $"читаем архив релиза {release}";
                    case State.Ready:
                        return $"релиз {release}" + (Problem != null ? $", ошибка: {Problem}" : "") +
                               (queue.Count > 0 ? $", в очереди {queue.Count}" : "");
                    default:
                        return Problem != null ? $"нет связи ({Problem}), пробуем снова" : "подключаемся";
                }
            }
        }

        public void Dispose()
        {
            foreach (OvertureRequest r in queue)
                if (r.Web != null)
                {
                    r.Web.Abort();
                    r.Web.Dispose();
                    r.Web = null;
                }
            queue.Clear();
            foreach (LeafFetch f in leafFetches.Values)
            {
                f.Web.Abort();
                f.Web.Dispose();
            }
            leafFetches.Clear();
            if (stateRequest != null)
            {
                stateRequest.Abort();
                stateRequest.Dispose();
                stateRequest = null;
            }
            active = 0;
        }
    }
}
