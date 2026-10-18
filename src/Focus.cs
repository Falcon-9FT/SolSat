// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;

namespace SolSatellite
{
    /// <summary>
    /// A point things are loaded around: the camera (what is on screen) or the vessel being
    /// flown (so that what is near it stays, colliders included, while the camera zooms out,
    /// and is there when it zooms back in). Kept as latitude, longitude (degrees) and altitude
    /// (m) on the planet: unlike world positions, these stay right while KSP moves the world
    /// origin, so a focus can be kept from an earlier frame.
    /// </summary>
    struct Focus
    {
        public bool Valid;
        public double Lat, Lon, Alt;

        public static Focus At(CelestialBody body, Vector3d world)
        {
            var f = new Focus { Lat = body.GetLatitude(world), Lon = body.GetLongitude(world), Alt = body.GetAltitude(world) };
            // A point at the planet's centre (or garbage) has no latitude: not a place to load around.
            f.Valid = !double.IsNaN(f.Lat) && !double.IsNaN(f.Lon) && !double.IsInfinity(f.Alt) && f.Alt > -body.Radius * 0.5;
            return f;
        }

        /// <summary>The active vessel, if it is flying at this body.</summary>
        public static Focus OfVessel(CelestialBody body)
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
                return default;
            Vessel v = FlightGlobals.ActiveVessel;
            return v != null && v.mainBody == body ? At(body, v.GetWorldPos3D()) : default;
        }

        /// <summary>Ground distance between two points, in metres on a sphere of this radius.</summary>
        public static double Distance(Focus a, Focus b, double radius)
        {
            double c = Vector3d.Dot(Geo.Direction(a.Lat, a.Lon), Geo.Direction(b.Lat, b.Lon));
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, c))) * radius;
        }

        /// <summary>Where the camera will be when it is back (map view closed): as far from the
        /// vessel now as it was from the vessel then, since KSP's flight camera keeps its
        /// place around the vessel.</summary>
        public static Focus Follow(Focus cameraThen, Focus vesselThen, Focus vesselNow, double radius)
        {
            if (!cameraThen.Valid || !vesselThen.Valid || !vesselNow.Valid)
                return cameraThen;
            var then = new TangentPlane(vesselThen.Lat, vesselThen.Lon, radius);
            if (!then.Project(Geo.Direction(cameraThen.Lat, cameraThen.Lon), out double x, out double y))
                return cameraThen;
            var now = new TangentPlane(vesselNow.Lat, vesselNow.Lon, radius);
            Geo.LatLon(now.Unproject(x, y), out double lat, out double lon);
            return new Focus { Valid = true, Lat = lat, Lon = lon, Alt = vesselNow.Alt + (cameraThen.Alt - vesselThen.Alt) };
        }
    }
}
