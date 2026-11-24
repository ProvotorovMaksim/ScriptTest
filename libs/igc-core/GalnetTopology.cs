using System;
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
            if (previous == null && _links.Count >= MaxNodes && !RemoveOldestExpired(now))
                return false;

            update.Neighbors.Sort();
            _links[node] = update;
            return true;
        }

        private bool RemoveOldestExpired(long now)
        {
            long oldestNode = 0;
            long oldestExpiry = now;
            foreach (var entry in _links)
            {
                if (entry.Value.ExpiresAt <= oldestExpiry)
                {
                    oldestNode = entry.Key;
                    oldestExpiry = entry.Value.ExpiresAt;
                }
            }
            return oldestNode != 0 && _links.Remove(oldestNode);
        }

        public bool TryGetNextHop(long source, long destination, long now, out long nextHop,
            long previousHop = 0, Func<long, bool> firstHopAvailable = null)
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
                    if (neighbor == previousHop || firstHop.ContainsKey(neighbor) ||
                        (node == source && firstHopAvailable != null && !firstHopAvailable(neighbor)) ||
                        !_links.TryGetValue(neighbor, out peer) ||
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
