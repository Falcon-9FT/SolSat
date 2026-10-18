// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;

namespace SolSatellite
{
    /// <summary>
    /// Coordinate helpers, all in double precision. Sol puts real-world latitude/longitude on
    /// the planet as the PQS-local direction (cos lat cos lon, sin lat, cos lat sin lon), the
    /// same convention KSP's own lat/lon uses.
    /// </summary>
    static class Geo
    {
        public const double MercatorMaxLat = 85.0511287798066;
        public const int TileSize = 256;
        /// <summary>Ground resolution of zoom-0 Web Mercator at the equator, metres/pixel.</summary>
        public const double MercatorResZ0 = 156543.03392804097;
        const double Deg = Math.PI / 180.0;

        public static Vector3d Direction(double lat, double lon)
        {
            double cl = Math.Cos(lat * Deg);
            return new Vector3d(cl * Math.Cos(lon * Deg), Math.Sin(lat * Deg), cl * Math.Sin(lon * Deg));
        }

        public static void LatLon(Vector3d dir, out double lat, out double lon)
        {
            lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.y))) / Deg;
            lon = Math.Atan2(dir.z, dir.x) / Deg;
        }

        public static double WorldPixels(int zoom) => (double)TileSize * (1L << zoom);

        public static double MercatorX(double lon, double world) => (lon + 180.0) / 360.0 * world;

        public static double MercatorY(double lat, double world)
        {
            double phi = Math.Max(-MercatorMaxLat, Math.Min(MercatorMaxLat, lat)) * Deg;
            return (1.0 - Math.Log(Math.Tan(phi) + 1.0 / Math.Cos(phi)) / Math.PI) * 0.5 * world;
        }

        public static double LonFromMercatorX(double x, double world) => x / world * 360.0 - 180.0;

        public static double LatFromMercatorY(double y, double world) =>
            Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / world))) / Deg;

        /// <summary>Lowest zoom whose imagery is about as sharp as texelMeters at this latitude
        /// (up to ~1.2x coarser - going one zoom finer would mean 4x the tiles for little gain).</summary>
        public static int ZoomFor(double lat, double texelMeters, int maxZoom)
        {
            double res0 = MercatorResZ0 * Math.Max(Math.Cos(lat * Deg), 1e-6);
            int zoom = (int)Math.Ceiling(Math.Log(res0 / texelMeters, 2.0) - 0.3);
            return Math.Max(1, Math.Min(maxZoom, zoom));
        }

        public static long TileKey(int z, int x, int y) => ((long)z << 42) | ((long)y << 21) | (long)x;
    }

    /// <summary>
    /// Plane tangent to the planet at an anchor point, with gnomonic (radial) projection. The
    /// terrain shader does the same projection from world positions, relative to the anchor.
    /// </summary>
    sealed class TangentPlane
    {
        public readonly double Lat, Lon, Radius;
        public readonly Vector3d Up, East, North; // PQS-local

        public TangentPlane(double lat, double lon, double radius)
        {
            Lat = lat;
            Lon = lon;
            Radius = radius;
            double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
            Up = Geo.Direction(lat, lon);
            East = new Vector3d(-Math.Sin(lo), 0.0, Math.Cos(lo));
            North = new Vector3d(-Math.Sin(la) * Math.Cos(lo), Math.Cos(la), -Math.Sin(la) * Math.Sin(lo));
        }

        public bool Project(Vector3d dir, out double x, out double y)
        {
            double up = Vector3d.Dot(dir, Up);
            if (up < 0.1)
            {
                x = y = 0;
                return false;
            }
            x = Radius * Vector3d.Dot(dir, East) / up;
            y = Radius * Vector3d.Dot(dir, North) / up;
            return true;
        }

        public Vector3d Unproject(double x, double y) => (Up * Radius + East * x + North * y).normalized;
    }
}
