// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

// SolSatellite.BurstPQS.dll: the real-terrain PQSMod for BurstPQS, which builds quads in
// parallel Burst-compiled jobs and would otherwise drop Earth back to KSP's own slow builds
// (it can't run a PQSMod's OnVertexBuildHeight there). A separate DLL so SolSatellite works
// without BurstPQS: KSP skips this one when BurstPQS isn't installed.

using System;
using BurstPQS;
using Unity.Burst;

[assembly: KSPAssembly("SolSatellite.BurstPQS", 1, 0)]
[assembly: KSPAssemblyDependency("SolSatellite", 1, 0)]
[assembly: KSPAssemblyDependency("BurstPQS", 0, 1)]
[assembly: KSPAssemblyDependency("KSPBurst", 1, 5)] // KSPBurst only compiles DLLs that depend on it

namespace SolSatellite.BurstAdapter
{
    [BurstCompile]
    [BatchPQSMod(typeof(PQSMod_SolSatDem))]
    public class DemBatchMod : BatchPQSMod<PQSMod_SolSatDem>
    {
        public DemBatchMod(PQSMod_SolSatDem mod) : base(mod) { }

        // No MapSOs to check.
        public override void OnSetup() { }

        public override unsafe void OnQuadPreBuild(PQ quad, BatchPQSJobSet jobSet)
        {
            base.OnQuadPreBuild(quad, jobSet);
            // The tiles this quad needs, held in memory until the job is disposed.
            if (!DemApi.Lease(quad, out DemLease lease))
                return;
            jobSet.Add(new HeightJob
            {
                Refs = (DemTileRef*)lease.Refs,
                Count = lease.Count,
                Exclusions = (DemExclusion*)lease.Exclusions,
                ExclusionCount = lease.ExclusionCount,
                Patches = (DemPatch*)lease.Patches,
                PatchCount = lease.PatchCount,
                Params = lease.Params,
                Handle = lease.Handle,
            });
        }

        [BurstCompile]
        unsafe struct HeightJob : IBatchPQSHeightJob, IDisposable
        {
            public DemTileRef* Refs;
            public int Count;
            public DemExclusion* Exclusions;
            public int ExclusionCount;
            public DemPatch* Patches;     // terrain patches over the quad (TerrainPatches)
            public int PatchCount;
            public DemParams Params;
            public int Handle;

            public void BuildHeights(in BuildHeightsData data)
            {
                double radius = data.sphere.radius;
                Vector3d* dirs = data.directionFromCenter.GetDataPtr();
                double* heights = data.vertHeight.GetDataPtr();
                int n = data.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    Vector3d d = dirs[i];
                    double h = DemSampler.Height(heights[i] - radius, d.x, d.y, d.z, Refs, Count,
                                                 Exclusions, ExclusionCount, ref Params);
                    heights[i] = radius + DemSampler.Patch(h, d.x, d.y, d.z, Patches, PatchCount, Exclusions, ExclusionCount, radius);
                }
            }

            // On the main thread, once the quad is built (or its build dropped).
            public void Dispose() => DemApi.Release(Handle);
        }
    }
}
