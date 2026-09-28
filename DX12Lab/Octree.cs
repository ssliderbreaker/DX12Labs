using System.Collections.Generic;
using System.Numerics;

namespace DX12Lab;

public class Octree
{
    private readonly OctreeNode _root;

    public Octree(BoundingBox worldBounds)
    {
        _root = new OctreeNode(worldBounds, 0);
    }

    public void Insert(int index, BoundingBox bounds) => _root.Insert(index, bounds);

    public void Query(Frustum frustum, List<int> results) => _root.Query(frustum, results);

    private class OctreeNode
    {
        private const int SplitThreshold = 16;
        private const int MaxDepth = 7;

        private struct Entry
        {
            public int Index;
            public BoundingBox Bounds;
        }

        public readonly BoundingBox Bounds;
        private readonly int _depth;
        private List<Entry> _entries = new();
        private OctreeNode[]? _children;

        public OctreeNode(BoundingBox bounds, int depth)
        {
            Bounds = bounds;
            _depth = depth;
        }

        public void Insert(int index, BoundingBox objBounds)
        {
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    if (child.Bounds.Contains(objBounds))
                    {
                        child.Insert(index, objBounds);
                        return;
                    }
                }
                _entries.Add(new Entry { Index = index, Bounds = objBounds });
                return;
            }

            _entries.Add(new Entry { Index = index, Bounds = objBounds });

            if (_entries.Count > SplitThreshold && _depth < MaxDepth)
                Split();
        }

        private void Split()
        {
            var center = Bounds.Center;
            var min = Bounds.Min;
            var max = Bounds.Max;

            _children = new OctreeNode[8];
            for (int i = 0; i < 8; i++)
            {
                var childMin = new Vector3(
                    (i & 1) == 0 ? min.X : center.X,
                    (i & 2) == 0 ? min.Y : center.Y,
                    (i & 4) == 0 ? min.Z : center.Z);
                var childMax = new Vector3(
                    (i & 1) == 0 ? center.X : max.X,
                    (i & 2) == 0 ? center.Y : max.Y,
                    (i & 4) == 0 ? center.Z : max.Z);

                _children[i] = new OctreeNode(new BoundingBox(childMin, childMax), _depth + 1);
            }

            var old = _entries;
            _entries = new List<Entry>();

            foreach (var e in old)
                Insert(e.Index, e.Bounds);
        }

        public void Query(Frustum frustum, List<int> results)
        {
            if (!frustum.Intersects(Bounds))
                return;

            foreach (var e in _entries)
                results.Add(e.Index);

            if (_children != null)
                foreach (var child in _children)
                    child.Query(frustum, results);
        }
    }
}