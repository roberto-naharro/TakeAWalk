using System.Collections.Generic;
using UnityEngine;

namespace TakeAWalk
{
    // Result of analysing the contiguous pedestrian-path "component" a segment belongs to.
    internal struct PathComponent
    {
        internal float Length;        // total contiguous length (metres), capped by MaxPathSegments
        internal bool Reachable;      // touches the walkable (road/sidewalk) network somewhere
        internal bool IsDeadEnd;      // reachable AND has a dead-end tip (a leaf where the path stops)
        internal ushort FarthestNode; // that tip - the leaf furthest (by path distance) from an entrance
        internal Vector3 FarthestPos; // its world position (where Half 2 plops the attraction)
    }

    // Bounded BFS over connected Beautification segments (via shared nodes). Used by Half 1 for
    // contiguous length and by Half 2 to find a dead-end path's far tip. Runs on the simulation
    // thread only, so the reusable static collections are safe (no concurrency).
    internal static class PathMetrics
    {
        private static readonly Queue<ushort> _queue = new Queue<ushort>(128);
        private static readonly HashSet<ushort> _visited = new HashSet<ushort>();   // segments
        private static readonly HashSet<ushort> _nodes = new HashSet<ushort>();
        private static readonly Queue<ushort> _nodeQueue = new Queue<ushort>(128);
        private static readonly Dictionary<ushort, float> _dist = new Dictionary<ushort, float>();
        // Depth-first walk ordering, so a tour route follows the whole path/loop, not one section.
        private static readonly Stack<ushort> _stack = new Stack<ushort>(128);
        private static readonly HashSet<ushort> _visitedNodes = new HashSet<ushort>();
        private static readonly List<ushort> _order = new List<ushort>(128);

        // Length only (cheap path for Half 1 when attractions are off).
        internal static float MeasureLength(NetManager nm, ushort startSegment, int maxSegments)
        {
            return Analyze(nm, startSegment, maxSegments, false).Length;
        }

        // Build a walking-tour route for the SINGLE-ENTRANCE scenic path containing startSegment
        // (a spur or a one-entrance loop). Walks the path depth-first from the entrance and lays
        // up to maxStops stops evenly along that walk, so the route covers the WHOLE path/loop, not
        // just a section. Fills `stops`, returns the entrance node id (a stable key) and the route
        // length; copies the component's segments into `componentSegments` for per-sweep dedup.
        // Returns 0 if the path is a through-path (2+ entrances), unreachable (0), or too small.
        internal static ushort TryBuildTour(NetManager nm, ushort startSegment, int maxSegments,
            int maxStops, float roadRadius, List<Vector3> stops, Dictionary<ushort, float> segLen,
            bool buildTour, out float length, out int entrances)
        {
            NetSegment[] segs = nm.m_segments.m_buffer;
            NetNode[] nodes = nm.m_nodes.m_buffer;
            length = 0f;
            entrances = 0;

            _queue.Clear();
            _visited.Clear();
            _nodes.Clear();
            _queue.Enqueue(startSegment);
            _visited.Add(startSegment);
            while (_queue.Count > 0)
            {
                ushort id = _queue.Dequeue();
                NetSegment seg = segs[id];
                length += SegmentLength(nodes, ref seg);
                _nodes.Add(seg.m_startNode);
                _nodes.Add(seg.m_endNode);
                if (_visited.Count >= maxSegments) break;
                ExpandNode(segs, nodes, seg.m_startNode, maxSegments);
                ExpandNode(segs, nodes, seg.m_endNode, maxSegments);
            }
            // Cache this component's length against every one of its segments (dedup + length
            // reuse): all segments in a component share the same total length, so the rest of the
            // component is O(1) this sweep instead of re-walking.
            if (segLen != null)
                foreach (ushort sid in _visited) segLen[sid] = length;

            if (!buildTour) return 0;

            // Count the path's "open ends" that meet the road network: leaf nodes (exactly one path
            // segment) next to a road. A spur has exactly ONE; a through-path has TWO or more and is
            // already walked by pedestrians moving between places, so we skip it. Counting only
            // leaves (not every nearby node) avoids mistaking a path running alongside a road for a
            // through-path. The lowest-id entrance is the stable start + dedup key.
            ushort entrance = 0;
            int entranceLeaves = 0;
            foreach (ushort nId in _nodes)
            {
                if (PathDegree(segs, nodes, nId) != 1) continue;
                if (!NearRoad(nm, nodes[nId].m_position, roadRadius)) continue;
                entranceLeaves++;
                if (entrance == 0 || nId < entrance) entrance = nId;
            }
            entrances = entranceLeaves;
            if (entranceLeaves >= 2) return 0;   // through-path: pedestrians already use it

            if (entranceLeaves == 0)
            {
                // No open end on a road - a pure loop connected at a junction. Accept it as a
                // single-entrance loop if it touches a road anywhere; else it is an isolated island.
                foreach (ushort nId in _nodes)
                    if (NearRoad(nm, nodes[nId].m_position, roadRadius) && (entrance == 0 || nId < entrance))
                        entrance = nId;
                if (entrance == 0) return 0;   // unreachable island - nobody can get to it
            }

            // Depth-first walk from the entrance, recording nodes in visit order. For a loop this
            // goes all the way around; for a spur it runs out to the tip (and through any branches).
            _order.Clear();
            _visitedNodes.Clear();
            _stack.Clear();
            _stack.Push(entrance);
            while (_stack.Count > 0)
            {
                ushort n = _stack.Pop();
                if (_visitedNodes.Contains(n)) continue;
                _visitedNodes.Add(n);
                _order.Add(n);
                NetNode node = nodes[n];
                for (int i = 0; i < 8; i++)
                {
                    ushort sid = node.GetSegment(i);
                    if (sid == 0 || !_visited.Contains(sid)) continue;
                    NetSegment seg = segs[sid];
                    ushort other = seg.m_startNode == n ? seg.m_endNode : seg.m_startNode;
                    if (other != 0 && !_visitedNodes.Contains(other)) _stack.Push(other);
                }
            }
            if (_order.Count < 3) return 0;

            // Lay up to maxStops stops evenly along the walk (always include the first and last).
            if (maxStops < 3) maxStops = 3;
            int step = _order.Count / maxStops;
            if (step < 1) step = 1;
            stops.Clear();
            for (int i = 0; i < _order.Count; i += step)
                stops.Add(nodes[_order[i]].m_position);
            Vector3 lastPos = nodes[_order[_order.Count - 1]].m_position;
            if (stops[stops.Count - 1] != lastPos)
                stops.Add(lastPos);
            return entrance;
        }

        // Full analysis. When needFarthest is false, only Length is filled.
        internal static PathComponent Analyze(NetManager nm, ushort startSegment, int maxSegments, bool needFarthest)
        {
            NetSegment[] segs = nm.m_segments.m_buffer;
            NetNode[] nodes = nm.m_nodes.m_buffer;

            _queue.Clear();
            _visited.Clear();
            _nodes.Clear();
            _queue.Enqueue(startSegment);
            _visited.Add(startSegment);

            PathComponent result = new PathComponent();
            float total = 0f;
            while (_queue.Count > 0)
            {
                ushort id = _queue.Dequeue();
                NetSegment seg = segs[id];
                total += SegmentLength(nodes, ref seg);
                _nodes.Add(seg.m_startNode);
                _nodes.Add(seg.m_endNode);
                if (_visited.Count >= maxSegments) break;
                ExpandNode(segs, nodes, seg.m_startNode, maxSegments);
                ExpandNode(segs, nodes, seg.m_endNode, maxSegments);
            }
            result.Length = total;
            if (!needFarthest) return result;

            // Entrance = a component node that also touches a non-path (road) segment, i.e. where
            // cims step onto the path from the walkable network. The component is reachable if it
            // has at least one. (A path you build connected to nothing has none, so cims can never
            // reach it - an attraction there would be useless.)
            ushort entrance = 0;
            foreach (ushort n in _nodes)
            {
                if (ConnectsToNetwork(segs, nodes, n))
                {
                    entrance = n;
                    break;
                }
            }
            result.Reachable = entrance != 0;
            if (!result.Reachable) return result;   // unreachable island - no attraction

            // A dead-end tip is a leaf: a node with exactly one path segment in the component and
            // no road. Put the attraction at the leaf furthest from the entrance, so a cim walks
            // the whole spur to it. A pure through-path has no leaf and gets nothing.
            FindFarthestLeaf(segs, nodes, entrance, ref result);
            return result;
        }

        // Distance BFS from the entrance; the farthest *leaf* (dead-end tip) is the attraction spot.
        private static void FindFarthestLeaf(NetSegment[] segs, NetNode[] nodes, ushort entrance, ref PathComponent result)
        {
            _dist.Clear();
            _nodeQueue.Clear();
            _dist[entrance] = 0f;
            _nodeQueue.Enqueue(entrance);

            ushort bestLeaf = 0;
            float bestDist = -1f;
            while (_nodeQueue.Count > 0)
            {
                ushort n = _nodeQueue.Dequeue();
                float baseDist = _dist[n];
                NetNode node = nodes[n];
                for (int i = 0; i < 8; i++)
                {
                    ushort sid = node.GetSegment(i);
                    if (sid == 0 || !_visited.Contains(sid)) continue;
                    NetSegment seg = segs[sid];
                    ushort other = seg.m_startNode == n ? seg.m_endNode : seg.m_startNode;
                    if (other == 0 || _dist.ContainsKey(other)) continue;
                    _dist[other] = baseDist + SegmentLength(nodes, ref seg);
                    _nodeQueue.Enqueue(other);
                }
                // A real dead-end tip: exactly one path segment overall (counted on the live graph,
                // not just the visited set, so a BFS-cap frontier node isn't mistaken for a leaf)
                // and not a road junction. The deepest such node is where the path stops.
                if (n != entrance && PathDegree(segs, nodes, n) == 1 &&
                    !ConnectsToNetwork(segs, nodes, n) && baseDist > bestDist)
                {
                    bestDist = baseDist;
                    bestLeaf = n;
                }
            }
            if (bestLeaf != 0)
            {
                result.IsDeadEnd = true;
                result.FarthestNode = bestLeaf;
                result.FarthestPos = nodes[bestLeaf].m_position;
            }
        }

        private static void ExpandNode(NetSegment[] segs, NetNode[] nodes, ushort nodeId, int maxSegments)
        {
            if (nodeId == 0) return;
            NetNode node = nodes[nodeId];
            for (int i = 0; i < 8; i++)
            {
                ushort sid = node.GetSegment(i);
                if (sid == 0 || _visited.Contains(sid)) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Created) == 0) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Deleted) != 0) continue;
                if (!IsPedestrianPath(segs[sid].Info)) continue;
                if (_visited.Count >= maxSegments) return;
                _visited.Add(sid);
                _queue.Enqueue(sid);
            }
        }

        // True if the node touches a created non-path segment (a road) - the path's link to the
        // rest of the walkable network.
        private static bool ConnectsToNetwork(NetSegment[] segs, NetNode[] nodes, ushort nodeId)
        {
            if (nodeId == 0) return false;
            NetNode node = nodes[nodeId];
            for (int i = 0; i < 8; i++)
            {
                ushort sid = node.GetSegment(i);
                if (sid == 0) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Created) == 0) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Deleted) != 0) continue;
                if (!IsPedestrianPath(segs[sid].Info)) return true;
            }
            return false;
        }

        // Number of created pedestrian-path segments touching the node (on the live graph).
        private static int PathDegree(NetSegment[] segs, NetNode[] nodes, ushort nodeId)
        {
            if (nodeId == 0) return 0;
            NetNode node = nodes[nodeId];
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort sid = node.GetSegment(i);
                if (sid == 0) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Created) == 0) continue;
                if ((segs[sid].m_flags & NetSegment.Flags.Deleted) != 0) continue;
                if (IsPedestrianPath(segs[sid].Info)) count++;
            }
            return count;
        }

        private static float SegmentLength(NetNode[] nodes, ref NetSegment seg)
        {
            Vector3 a = nodes[seg.m_startNode].m_position;
            Vector3 b = nodes[seg.m_endNode].m_position;
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // A walkable pedestrian path. Beautification covers decorative pedestrian paths,
        // park paths, and Plazas & Promenades park paths. (Roads are excluded by design.)
        internal static bool IsPedestrianPath(NetInfo info)
        {
            return info != null && info.m_class != null &&
                   info.m_class.m_service == ItemClass.Service.Beautification;
        }

        // A leaf: a node with exactly one pedestrian-path segment (a path endpoint).
        internal static bool IsLeaf(NetManager nm, ushort nodeId)
        {
            if (nodeId == 0) return false;
            return PathDegree(nm.m_segments.m_buffer, nm.m_nodes.m_buffer, nodeId) == 1;
        }

        // A genuine dead-end tip: a node with exactly one path segment that is NOT next to a road.
        // (In CS1 paths don't share nodes with roads, so the road-connected end of a spur is also
        // path-degree 1; we tell it apart by road proximity.) roadRadius in metres.
        internal static bool IsDeadEndTip(NetManager nm, ushort nodeId, float roadRadius)
        {
            if (nodeId == 0) return false;
            NetSegment[] segs = nm.m_segments.m_buffer;
            NetNode[] nodes = nm.m_nodes.m_buffer;
            if (PathDegree(segs, nodes, nodeId) != 1) return false;
            return !NearRoad(nm, nodes[nodeId].m_position, roadRadius);
        }

        // True if a Road-service segment passes within radius of pos (segment grid: 270^2 @ 64 m).
        private static bool NearRoad(NetManager nm, Vector3 pos, float radius)
        {
            ushort[] grid = nm.m_segmentGrid;
            NetSegment[] segs = nm.m_segments.m_buffer;
            NetNode[] nodes = nm.m_nodes.m_buffer;
            float r2 = radius * radius;
            int gx = GridClamp((int)(pos.x / 64f + 135f));
            int gz = GridClamp((int)(pos.z / 64f + 135f));

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = gx + dx, cz = gz + dz;
                    if (cx < 0 || cx > 269 || cz < 0 || cz > 269) continue;
                    ushort sid = grid[cz * 270 + cx];
                    int guard = 0;
                    while (sid != 0 && guard++ < 32768)
                    {
                        NetSegment seg = segs[sid];
                        if ((seg.m_flags & NetSegment.Flags.Created) != 0 &&
                            (seg.m_flags & NetSegment.Flags.Deleted) == 0)
                        {
                            NetInfo info = seg.Info;
                            if (info != null && info.m_class != null &&
                                info.m_class.m_service == ItemClass.Service.Road &&
                                PointSegmentDistSq(nodes[seg.m_startNode].m_position,
                                                   nodes[seg.m_endNode].m_position, pos) <= r2)
                                return true;
                        }
                        sid = seg.m_nextGridSegment;
                    }
                }
            }
            return false;
        }

        private static float PointSegmentDistSq(Vector3 a, Vector3 b, Vector3 p)
        {
            float abx = b.x - a.x, abz = b.z - a.z;
            float apx = p.x - a.x, apz = p.z - a.z;
            float len2 = abx * abx + abz * abz;
            float t = len2 > 0f ? (apx * abx + apz * abz) / len2 : 0f;
            if (t < 0f) t = 0f; if (t > 1f) t = 1f;
            float dx = apx - t * abx, dz = apz - t * abz;
            return dx * dx + dz * dz;
        }

        private static int GridClamp(int v)
        {
            if (v < 0) return 0;
            if (v > 269) return 269;
            return v;
        }
    }
}
