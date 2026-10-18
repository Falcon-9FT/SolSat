// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolSatellite
{
    /// <summary>
    /// Unity meshes from the meshers' data, set in one go: the vertex buffer's layout once, each
    /// stream copied whole, 32-bit indices likewise, the bounds given. One attribute at a time
    /// (SetVertices, SetNormals, SetUVs...) has Unity lay the whole vertex buffer out anew with
    /// every attribute added: a central Tokyo tile's buildings took 75-80 ms of the main thread
    /// that way and its 3D roads 67 ms; set like this 18-21 and 10-11 ms, the same data
    /// (Unity 2019.4.18 in batch mode, 2026-10-02).
    /// </summary>
    static class MeshUpload
    {
        // Indices come from the meshers, in range; the bounds are known.
        const MeshUpdateFlags Quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds |
                                      MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds;

        // Position, colour and texture coordinates each in a stream of its own (lamps, light
        // pools, ribbons): their lists go in as they are.
        static readonly VertexAttributeDescriptor[] ThreeStreams =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 2),
        };

        static readonly VertexAttributeDescriptor[] FourStreams =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 3),
        };

        /// <summary>A mesh of one interleaved vertex stream (its layout given).</summary>
        public static Mesh Interleaved<T>(string name, List<T> vertices, VertexAttributeDescriptor[] layout, List<int> indices, Bounds bounds)
            where T : struct
        {
            var mesh = new Mesh { name = name };
            int n = vertices.Count;
            if (n == 0 || indices.Count == 0)
                return mesh; // nothing to draw: no zero-sized buffers for the driver
            mesh.SetVertexBufferParams(n, layout);
            mesh.SetVertexBufferData(vertices, 0, 0, n, 0, Quiet);
            Indices(mesh, indices, n, bounds);
            return mesh;
        }

        /// <summary>A mesh of positions, colours and one or two sets of texture coordinates
        /// (uv1 may be null), each list a stream.</summary>
        public static Mesh Streams(string name, List<Vector3> positions, List<Color32> colours, List<Vector4> uv0, List<Vector4> uv1,
                                   List<int> indices, Bounds bounds)
        {
            var mesh = new Mesh { name = name };
            int n = positions.Count;
            if (n == 0 || indices.Count == 0)
                return mesh;
            mesh.SetVertexBufferParams(n, uv1 != null ? FourStreams : ThreeStreams);
            mesh.SetVertexBufferData(positions, 0, 0, n, 0, Quiet);
            mesh.SetVertexBufferData(colours, 0, 0, n, 1, Quiet);
            mesh.SetVertexBufferData(uv0, 0, 0, n, 2, Quiet);
            if (uv1 != null)
                mesh.SetVertexBufferData(uv1, 0, 0, n, 3, Quiet);
            Indices(mesh, indices, n, bounds);
            return mesh;
        }

        static void Indices(Mesh mesh, List<int> indices, int vertexCount, Bounds bounds)
        {
            int m = indices.Count;
            mesh.SetIndexBufferParams(m, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, m, Quiet);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, m) { bounds = bounds, vertexCount = vertexCount }, Quiet);
            mesh.bounds = bounds;
        }
    }
}
