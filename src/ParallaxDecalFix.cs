// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parallax.PQS_Mods;
using UnityEngine;

namespace SolSatellite
{
    /// <summary>
    /// Parallax keeps, for each quad near a decal that takes scatters away (Sol's launch sites),
    /// the decal that masks it (PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad): put in
    /// by OnQuadPreBuild, taken out by OnQuadDestroy - but only if the quad's centre is still
    /// within the decal's reach then, worked out again from where the quad is by that time. A
    /// quad that has moved a little in between (KSP repositions the last level's quads; the real
    /// terrain's rebuilds put them right before building again) stays in the table, and the next
    /// time it is built Parallax takes its own old entry for a second decal: the dialog
    /// "Critical Parallax Error: Multiple MapDecalVertexRemoveScatter mods on the same quad"
    /// (2026-10-03, after teleports). A Harmony prefix on OnQuadPreBuild: an entry of the same
    /// decal is kept as it is, one of a decal that no longer reaches the quad is replaced, and
    /// only two decals that both reach it go on to Parallax's own error. Parallax's files are
    /// not touched.
    /// </summary>
    static class ParallaxDecalFix
    {
        static AccessTools.FieldRef<PQSMod_MapDecalVertexRemoveScatter, double> inclusion;
        static bool applied, failed;

        /// <summary>Stale entries set right.</summary>
        public static int Repairs { get; private set; }
        public static bool Active => applied && !failed;

        public static void Apply()
        {
            if (applied)
                return;
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PQSMod_MapDecalVertexRemoveScatter), "OnQuadPreBuild", new[] { typeof(PQ) });
                inclusion = AccessTools.FieldRefAccess<PQSMod_MapDecalVertexRemoveScatter, double>("inclusionAngle");
                if (original == null || inclusion == null)
                {
                    Debug.LogWarning("[SolSatellite] Parallax's scatter decal isn't what the decal fix expects; not patched");
                    return;
                }
                new Harmony("SolSatellite.ParallaxDecalFix").Patch(original,
                    prefix: new HarmonyMethod(typeof(ParallaxDecalFix).GetMethod(nameof(BeforePreBuild), BindingFlags.Static | BindingFlags.NonPublic)));
                applied = true;
                Debug.Log("[SolSatellite] Parallax scatter decal table fix applied");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SolSatellite] Parallax decal fix not applied: " + e.Message);
            }
        }

        /// <summary>A quad about to be built again in place (Dem.Rebuild): out of the table, so
        /// building puts it back under the decal that reaches it now.</summary>
        public static void Forget(PQ quad)
        {
            try
            {
                if (quad != null)
                    PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad?.Remove(quad);
            }
            catch (Exception)
            {
            }
        }

        // false: handled here, Parallax's OnQuadPreBuild is skipped.
        static bool BeforePreBuild(PQSMod_MapDecalVertexRemoveScatter __instance, PQ quad)
        {
            if (failed)
                return true;
            try
            {
                Dictionary<PQ, PQSMod_MapDecalVertexRemoveScatter> table = PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad;
                if (quad == null || table == null || !table.TryGetValue(quad, out PQSMod_MapDecalVertexRemoveScatter had) || !Reaches(__instance, quad))
                    return true; // as Parallax does it: nothing in the way
                if (ReferenceEquals(had, __instance))
                {
                    Repairs++;
                    return false; // its own old entry: as good as putting it in
                }
                if (had == null || !Reaches(had, quad))
                {
                    table[quad] = __instance; // the old decal's entry outlived its reach
                    Repairs++;
                    return false;
                }
                return true; // two decals reach this quad: Parallax's error stands
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning("[SolSatellite] Parallax decal fix off: " + e.Message);
                return true;
            }
        }

        /// <summary>The decal's own test (OnQuadPreBuild, OnQuadDestroy).</summary>
        static bool Reaches(PQSMod_MapDecalVertexRemoveScatter m, PQ quad) =>
            Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, m.normalisedPosition)) < inclusion(m);
    }
}
