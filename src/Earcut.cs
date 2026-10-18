// Polygon triangulation: a C# port of mapbox/earcut 2.2.4.
//
// ISC License
// Copyright (c) 2016, Mapbox
// Permission to use, copy, modify, and/or distribute this software for any purpose with or
// without fee is hereby granted, provided that the above copyright notice and this permission
// notice appear in all copies.
// THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
// SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL
// ISC BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
// WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
// NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
// PERFORMANCE OF THIS SOFTWARE.

using System;
using System.Collections.Generic;

namespace SolSatellite
{
    static class Earcut
    {
        sealed class Node
        {
            public int I;          // vertex index
            public double X, Y;
            public Node Prev, Next;
            public int Z;          // z-order curve value
            public Node PrevZ, NextZ;
            public bool Steiner;

            public Node(int i, double x, double y)
            {
                I = i;
                X = x;
                Y = y;
            }
        }

        // Nodes are reused from one polygon to the next (per thread): a city tile's buildings
        // made millions of them, megabytes of garbage a tile. Polygons bigger than ArenaMax
        // points get the rest new, and those aren't kept.
        const int ArenaMax = 16384;
        [ThreadStatic] static List<Node> arena;
        [ThreadStatic] static int arenaUsed;
        [ThreadStatic] static List<Node> holeQueue;

        static Node NewNode(int i, double x, double y)
        {
            List<Node> a = arena ??= new List<Node>(1024);
            if (arenaUsed < a.Count)
            {
                Node p = a[arenaUsed++];
                p.I = i;
                p.X = x;
                p.Y = y;
                p.Prev = p.Next = p.PrevZ = p.NextZ = null;
                p.Z = 0;
                p.Steiner = false;
                return p;
            }
            var n = new Node(i, x, y);
            if (a.Count < ArenaMax)
            {
                a.Add(n);
                arenaUsed++;
            }
            return n;
        }

        /// <summary>
        /// Triangulates a polygon given as flat x,y pairs: the outer ring first, then the holes,
        /// each starting at the vertex index listed in holeStarts. Appends vertex index triples.
        /// </summary>
        public static void Triangulate(double[] data, int vertexCount, List<int> holeStarts, List<int> triangles)
        {
            arenaUsed = 0; // the last polygon's nodes are free again
            bool hasHoles = holeStarts != null && holeStarts.Count > 0;
            int outerLen = (hasHoles ? holeStarts[0] : vertexCount) * 2;
            Node outerNode = LinkedList(data, 0, outerLen, true);
            if (outerNode == null || outerNode.Next == outerNode.Prev)
                return;

            if (hasHoles)
                outerNode = EliminateHoles(data, vertexCount * 2, holeStarts, outerNode);

            double minX = 0, minY = 0, invSize = 0;
            // For bigger shapes, a z-order curve hash speeds up the ear tests.
            if (vertexCount > 80)
            {
                minX = double.MaxValue;
                minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < outerLen; i += 2)
                {
                    double x = data[i], y = data[i + 1];
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
                invSize = Math.Max(maxX - minX, maxY - minY);
                invSize = invSize != 0 ? 32767 / invSize : 0;
            }

            EarcutLinked(outerNode, triangles, minX, minY, invSize, 0);
        }

        // Creates a circular doubly linked list from polygon points in the specified winding order.
        static Node LinkedList(double[] data, int start, int end, bool clockwise)
        {
            Node last = null;
            if (clockwise == (SignedArea(data, start, end) > 0))
            {
                for (int i = start; i < end; i += 2)
                    last = InsertNode(i / 2, data[i], data[i + 1], last);
            }
            else
            {
                for (int i = end - 2; i >= start; i -= 2)
                    last = InsertNode(i / 2, data[i], data[i + 1], last);
            }
            if (last != null && EqualPoints(last, last.Next))
            {
                RemoveNode(last);
                last = last.Next;
            }
            return last;
        }

        // Eliminates colinear or duplicate points.
        static Node FilterPoints(Node start, Node end = null)
        {
            if (start == null)
                return null;
            if (end == null)
                end = start;
            Node p = start;
            bool again;
            do
            {
                again = false;
                if (!p.Steiner && (EqualPoints(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
                {
                    RemoveNode(p);
                    p = end = p.Prev;
                    if (p == p.Next)
                        break;
                    again = true;
                }
                else
                {
                    p = p.Next;
                }
            } while (again || p != end);
            return end;
        }

        // Main ear slicing loop which triangulates a polygon (given as a linked list).
        static void EarcutLinked(Node ear, List<int> triangles, double minX, double minY, double invSize, int pass)
        {
            if (ear == null)
                return;
            if (pass == 0 && invSize != 0)
                IndexCurve(ear, minX, minY, invSize);

            Node stop = ear;
            while (ear.Prev != ear.Next)
            {
                Node prev = ear.Prev, next = ear.Next;
                if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
                {
                    triangles.Add(prev.I);
                    triangles.Add(ear.I);
                    triangles.Add(next.I);
                    RemoveNode(ear);
                    // Skipping the next vertex leads to fewer sliver triangles.
                    ear = next.Next;
                    stop = next.Next;
                    continue;
                }
                ear = next;

                // Looped through the whole remaining polygon without finding any more ears.
                if (ear == stop)
                {
                    if (pass == 0)
                    {
                        EarcutLinked(FilterPoints(ear), triangles, minX, minY, invSize, 1);
                    }
                    else if (pass == 1)
                    {
                        ear = CureLocalIntersections(FilterPoints(ear), triangles);
                        EarcutLinked(ear, triangles, minX, minY, invSize, 2);
                    }
                    else if (pass == 2)
                    {
                        SplitEarcut(ear, triangles, minX, minY, invSize);
                    }
                    break;
                }
            }
        }

        // Whether a polygon node forms a valid ear with its neighbours.
        static bool IsEar(Node ear)
        {
            Node a = ear.Prev, b = ear, c = ear.Next;
            if (Area(a, b, c) >= 0)
                return false; // reflex, can't be an ear

            double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
            double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
            double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));

            Node p = c.Next;
            while (p != a)
            {
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                    return false;
                p = p.Next;
            }
            return true;
        }

        static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
        {
            Node a = ear.Prev, b = ear, c = ear.Next;
            if (Area(a, b, c) >= 0)
                return false;

            double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
            double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
            double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));

            // z-order range for the current triangle's bbox
            int minZ = ZOrder(x0, y0, minX, minY, invSize);
            int maxZ = ZOrder(x1, y1, minX, minY, invSize);

            Node p = ear.PrevZ, n = ear.NextZ;
            // Look for points inside the triangle in both directions.
            while (p != null && p.Z >= minZ && n != null && n.Z <= maxZ)
            {
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p != a && p != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                    return false;
                p = p.PrevZ;

                if (n.X >= x0 && n.X <= x1 && n.Y >= y0 && n.Y <= y1 && n != a && n != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
                    return false;
                n = n.NextZ;
            }
            // Remaining points in decreasing z-order.
            while (p != null && p.Z >= minZ)
            {
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p != a && p != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                    return false;
                p = p.PrevZ;
            }
            // Remaining points in increasing z-order.
            while (n != null && n.Z <= maxZ)
            {
                if (n.X >= x0 && n.X <= x1 && n.Y >= y0 && n.Y <= y1 && n != a && n != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
                    return false;
                n = n.NextZ;
            }
            return true;
        }

        // Goes through all polygon nodes and cures small local self-intersections.
        static Node CureLocalIntersections(Node start, List<int> triangles)
        {
            Node p = start;
            do
            {
                Node a = p.Prev, b = p.Next.Next;
                if (!EqualPoints(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
                {
                    triangles.Add(a.I);
                    triangles.Add(p.I);
                    triangles.Add(b.I);
                    RemoveNode(p);
                    RemoveNode(p.Next);
                    p = start = b;
                }
                p = p.Next;
            } while (p != start);
            return FilterPoints(p);
        }

        // Tries splitting the polygon into two and triangulating them independently.
        static void SplitEarcut(Node start, List<int> triangles, double minX, double minY, double invSize)
        {
            Node a = start;
            do
            {
                Node b = a.Next.Next;
                while (b != a.Prev)
                {
                    if (a.I != b.I && IsValidDiagonal(a, b))
                    {
                        Node c = SplitPolygon(a, b);
                        a = FilterPoints(a, a.Next);
                        c = FilterPoints(c, c.Next);
                        EarcutLinked(a, triangles, minX, minY, invSize, 0);
                        EarcutLinked(c, triangles, minX, minY, invSize, 0);
                        return;
                    }
                    b = b.Next;
                }
                a = a.Next;
            } while (a != start);
        }

        // Links every hole into the outer loop, producing a single-ring polygon without holes.
        static Node EliminateHoles(double[] data, int dataLength, List<int> holeStarts, Node outerNode)
        {
            List<Node> queue = holeQueue ??= new List<Node>();
            queue.Clear();
            for (int i = 0; i < holeStarts.Count; i++)
            {
                int start = holeStarts[i] * 2;
                int end = i < holeStarts.Count - 1 ? holeStarts[i + 1] * 2 : dataLength;
                Node list = LinkedList(data, start, end, false);
                if (list == null)
                    continue;
                if (list == list.Next)
                    list.Steiner = true;
                queue.Add(GetLeftmost(list));
            }
            queue.Sort((a, b) => a.X.CompareTo(b.X));
            // Holes from left to right.
            foreach (Node hole in queue)
                outerNode = EliminateHole(hole, outerNode);
            queue.Clear();
            return outerNode;
        }

        // Finds a bridge between a hole and the outer polygon and links it.
        static Node EliminateHole(Node hole, Node outerNode)
        {
            Node bridge = FindHoleBridge(hole, outerNode);
            if (bridge == null)
                return outerNode;
            Node bridgeReverse = SplitPolygon(bridge, hole);
            FilterPoints(bridgeReverse, bridgeReverse.Next);
            return FilterPoints(bridge, bridge.Next);
        }

        // David Eberly's algorithm for finding a bridge between a hole and the outer polygon.
        static Node FindHoleBridge(Node hole, Node outerNode)
        {
            Node p = outerNode, m = null;
            double hx = hole.X, hy = hole.Y, qx = double.NegativeInfinity;

            // A segment crossed by a ray from the hole's leftmost point to the left; the
            // segment's endpoint with lesser x is a potential connection point.
            do
            {
                if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
                {
                    double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                    if (x <= hx && x > qx)
                    {
                        qx = x;
                        m = p.X < p.Next.X ? p : p.Next;
                        if (x == hx)
                            return m; // hole touches the outer segment; pick its leftmost endpoint
                    }
                }
                p = p.Next;
            } while (p != outerNode);

            if (m == null)
                return null;

            // Points inside the triangle of hole point, segment intersection and endpoint make the
            // connection invalid; then take the one with the smallest angle to the ray instead.
            Node stop = m;
            double mx = m.X, my = m.Y, tanMin = double.PositiveInfinity;
            p = m;
            do
            {
                if (hx >= p.X && p.X >= mx && hx != p.X &&
                    PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
                {
                    double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                    if (LocallyInside(p, hole) &&
                        (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
                    {
                        m = p;
                        tanMin = tan;
                    }
                }
                p = p.Next;
            } while (p != stop);
            return m;
        }

        // Whether the sector in vertex m contains the sector in vertex p in the same coordinates.
        static bool SectorContainsSector(Node m, Node p) =>
            Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;

        // Interlinks polygon nodes in z-order.
        static void IndexCurve(Node start, double minX, double minY, double invSize)
        {
            Node p = start;
            do
            {
                if (p.Z == 0)
                    p.Z = ZOrder(p.X, p.Y, minX, minY, invSize);
                p.PrevZ = p.Prev;
                p.NextZ = p.Next;
                p = p.Next;
            } while (p != start);
            p.PrevZ.NextZ = null;
            p.PrevZ = null;
            SortLinked(p);
        }

        // Simon Tatham's linked list merge sort.
        static Node SortLinked(Node list)
        {
            int inSize = 1, numMerges;
            do
            {
                Node p = list, tail = null;
                list = null;
                numMerges = 0;
                while (p != null)
                {
                    numMerges++;
                    Node q = p;
                    int pSize = 0;
                    for (int i = 0; i < inSize; i++)
                    {
                        pSize++;
                        q = q.NextZ;
                        if (q == null)
                            break;
                    }
                    int qSize = inSize;
                    while (pSize > 0 || (qSize > 0 && q != null))
                    {
                        Node e;
                        if (pSize != 0 && (qSize == 0 || q == null || p.Z <= q.Z))
                        {
                            e = p;
                            p = p.NextZ;
                            pSize--;
                        }
                        else
                        {
                            e = q;
                            q = q.NextZ;
                            qSize--;
                        }
                        if (tail != null)
                            tail.NextZ = e;
                        else
                            list = e;
                        e.PrevZ = tail;
                        tail = e;
                    }
                    p = q;
                }
                tail.NextZ = null;
                inSize *= 2;
            } while (numMerges > 1);
            return list;
        }

        // z-order of a point given coords and the inverse of the longer side of the data bbox.
        static int ZOrder(double px, double py, double minX, double minY, double invSize)
        {
            int x = (int)((px - minX) * invSize);
            int y = (int)((py - minY) * invSize);
            x = (x | (x << 8)) & 0x00FF00FF;
            x = (x | (x << 4)) & 0x0F0F0F0F;
            x = (x | (x << 2)) & 0x33333333;
            x = (x | (x << 1)) & 0x55555555;
            y = (y | (y << 8)) & 0x00FF00FF;
            y = (y | (y << 4)) & 0x0F0F0F0F;
            y = (y | (y << 2)) & 0x33333333;
            y = (y | (y << 1)) & 0x55555555;
            return x | (y << 1);
        }

        static Node GetLeftmost(Node start)
        {
            Node p = start, leftmost = start;
            do
            {
                if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y))
                    leftmost = p;
                p = p.Next;
            } while (p != start);
            return leftmost;
        }

        static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
            (cx - px) * (ay - py) >= (ax - px) * (cy - py) &&
            (ax - px) * (by - py) >= (bx - px) * (ay - py) &&
            (bx - px) * (cy - py) >= (cx - px) * (by - py);

        // Whether a diagonal between two polygon nodes is valid (lies in the polygon interior).
        static bool IsValidDiagonal(Node a, Node b) =>
            a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b) &&
            (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) &&
             (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0) ||
             EqualPoints(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0);

        static double Area(Node p, Node q, Node r) =>
            (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);

        static bool EqualPoints(Node p1, Node p2) => p1.X == p2.X && p1.Y == p2.Y;

        static bool Intersects(Node p1, Node q1, Node p2, Node q2)
        {
            int o1 = Math.Sign(Area(p1, q1, p2));
            int o2 = Math.Sign(Area(p1, q1, q2));
            int o3 = Math.Sign(Area(p2, q2, p1));
            int o4 = Math.Sign(Area(p2, q2, q1));
            if (o1 != o2 && o3 != o4)
                return true;
            if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
            if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
            if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
            if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
            return false;
        }

        // For collinear points p, q, r: whether q lies on segment pr.
        static bool OnSegment(Node p, Node q, Node r) =>
            q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

        static bool IntersectsPolygon(Node a, Node b)
        {
            Node p = a;
            do
            {
                if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
                    return true;
                p = p.Next;
            } while (p != a);
            return false;
        }

        static bool LocallyInside(Node a, Node b) =>
            Area(a.Prev, a, a.Next) < 0
                ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0
                : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

        static bool MiddleInside(Node a, Node b)
        {
            Node p = a;
            bool inside = false;
            double px = (a.X + b.X) / 2, py = (a.Y + b.Y) / 2;
            do
            {
                if (((p.Y > py) != (p.Next.Y > py)) && p.Next.Y != p.Y &&
                    (px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X))
                    inside = !inside;
                p = p.Next;
            } while (p != a);
            return inside;
        }

        // Links two polygon vertices with a bridge: splits the polygon in two if they are on the
        // same ring, or merges a hole into the outer ring.
        static Node SplitPolygon(Node a, Node b)
        {
            Node a2 = NewNode(a.I, a.X, a.Y);
            Node b2 = NewNode(b.I, b.X, b.Y);
            Node an = a.Next, bp = b.Prev;
            a.Next = b;
            b.Prev = a;
            a2.Next = an;
            an.Prev = a2;
            b2.Next = a2;
            a2.Prev = b2;
            bp.Next = b2;
            b2.Prev = bp;
            return b2;
        }

        static Node InsertNode(int i, double x, double y, Node last)
        {
            Node p = NewNode(i, x, y);
            if (last == null)
            {
                p.Prev = p;
                p.Next = p;
            }
            else
            {
                p.Next = last.Next;
                p.Prev = last;
                last.Next.Prev = p;
                last.Next = p;
            }
            return p;
        }

        static void RemoveNode(Node p)
        {
            p.Next.Prev = p.Prev;
            p.Prev.Next = p.Next;
            if (p.PrevZ != null)
                p.PrevZ.NextZ = p.NextZ;
            if (p.NextZ != null)
                p.NextZ.PrevZ = p.PrevZ;
        }

        static double SignedArea(double[] data, int start, int end)
        {
            double sum = 0;
            for (int i = start, j = end - 2; i < end; i += 2)
            {
                sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]);
                j = i;
            }
            return sum;
        }
    }
}
