using System.Collections.Generic;

namespace SpaceEngineers.Libs.IgcCore
{
    public sealed class GalnetTopology
    {
        private const int MaxNodes = 128;
        private const int MaxNeighbors = 32;
        private readonly Dictionary<long, Advertisement> _links = new Dictionary<long, Advertisement>();

        private sealed class Advertisement
        {
            public long Generation;
            public long Sequence;
            public long ExpiresAt;
            public readonly List<long> Neighbors = new List<long>();
        }

        public bool Observe(long node, long generation, long sequence, long expiresAt,
            IEnumerable<long> neighbors, long now)
        {
            if (node == 0 || generation < 0 || sequence < 0 || expiresAt <= now || neighbors == null)
                return false;

            Advertisement previous;
            if (_links.TryGetValue(node, out previous))
            {
                if (generation < previous.Generation ||
                    (generation == previous.Generation && sequence <= previous.Sequence))
                    return false;
            }
            else if (_links.Count >= MaxNodes)
                return false;

            var update = new Advertisement { Generation = generation, Sequence = sequence, ExpiresAt = expiresAt };
            foreach (var neighbor in neighbors)
            {
                if (neighbor != 0 && neighbor != node && !update.Neighbors.Contains(neighbor))
                {
                    if (update.Neighbors.Count >= MaxNeighbors)
                        return false;
                    update.Neighbors.Add(neighbor);
                }
            }
            update.Neighbors.Sort();
            _links[node] = update;
            return true;
        }

        public bool TryGetNextHop(long source, long destination, long now, out long nextHop)
        {
            nextHop = 0;
            if (source == destination)
                return false;

            var firstHop = new Dictionary<long, long> { { source, source } };
            var queue = new Queue<long>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                long node = queue.Dequeue();
                Advertisement current;
                if (!_links.TryGetValue(node, out current) || current.ExpiresAt <= now)
                    continue;

                foreach (var neighbor in current.Neighbors)
                {
                    Advertisement peer;
                    if (firstHop.ContainsKey(neighbor) || !_links.TryGetValue(neighbor, out peer) ||
                        peer.ExpiresAt <= now || !peer.Neighbors.Contains(node))
                        continue;

                    long hop = node == source ? neighbor : firstHop[node];
                    if (neighbor == destination)
                    {
                        nextHop = hop;
                        return true;
                    }
                    firstHop.Add(neighbor, hop);
                    queue.Enqueue(neighbor);
                }
            }
            return false;
        }
    }
}
