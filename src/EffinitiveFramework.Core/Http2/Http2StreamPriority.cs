namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// HTTP/2 stream priority information (RFC 7540 §5.3)
/// Manages stream dependencies and weights for prioritized scheduling
/// </summary>
public class Http2StreamPriority
{
    /// <summary>
    /// Stream ID this stream depends on (0 = no dependency)
    /// </summary>
    public int DependsOn { get; set; }

    /// <summary>
    /// Stream weight (1-256, default 16)
    /// Higher weight = higher priority
    /// </summary>
    public int Weight { get; set; } = 16;

    /// <summary>
    /// Exclusive flag - if true, makes this stream sole child of parent
    /// </summary>
    public bool Exclusive { get; set; }

    /// <summary>
    /// Parse priority from HEADERS frame payload
    /// </summary>
    public static Http2StreamPriority Parse(ReadOnlySpan<byte> priorityData)
    {
        if (priorityData.Length < 5)
            throw new ArgumentException("Priority data must be 5 bytes");

        // First 4 bytes: Exclusive bit + stream dependency (31 bits)
        var dependencyValue = (priorityData[0] << 24) | 
                             (priorityData[1] << 16) | 
                             (priorityData[2] << 8) | 
                             priorityData[3];

        var exclusive = (dependencyValue & 0x80000000) != 0;
        var dependsOn = dependencyValue & 0x7FFFFFFF;

        // 5th byte: weight (1-256, stored as 0-255)
        var weight = priorityData[4] + 1;

        return new Http2StreamPriority
        {
            DependsOn = dependsOn,
            Weight = weight,
            Exclusive = exclusive
        };
    }

    /// <summary>
    /// Encode priority to bytes for PRIORITY frame
    /// </summary>
    public byte[] Encode()
    {
        var bytes = new byte[5];
        
        var dependencyValue = DependsOn & 0x7FFFFFFF;
        if (Exclusive)
        {
            dependencyValue |= unchecked((int)0x80000000);
        }

        bytes[0] = (byte)(dependencyValue >> 24);
        bytes[1] = (byte)(dependencyValue >> 16);
        bytes[2] = (byte)(dependencyValue >> 8);
        bytes[3] = (byte)dependencyValue;
        bytes[4] = (byte)(Weight - 1);

        return bytes;
    }
}

/// <summary>
/// Stream priority scheduler using weighted round-robin
/// Implements RFC 7540 §5.3 priority scheme
/// </summary>
public class StreamPriorityScheduler
{
    private readonly Dictionary<int, StreamPriorityNode> _streams = new();
    private readonly object _lock = new();

    private class StreamPriorityNode
    {
        public int StreamId { get; set; }
        public Http2StreamPriority Priority { get; set; } = new();
        public List<int> Children { get; set; } = new();
        public int ParentId { get; set; }
    }

    /// <summary>
    /// Register a stream with priority information
    /// </summary>
    public void RegisterStream(int streamId, Http2StreamPriority? priority = null)
    {
        lock (_lock)
        {
            if (_streams.ContainsKey(streamId))
                return;

            var node = new StreamPriorityNode
            {
                StreamId = streamId,
                Priority = priority ?? new Http2StreamPriority()
            };

            _streams[streamId] = node;
            
            if (priority != null && priority.DependsOn > 0)
            {
                UpdateDependency(streamId, priority.DependsOn, priority.Exclusive);
            }
        }
    }

    /// <summary>
    /// Update stream priority
    /// </summary>
    public void UpdatePriority(int streamId, Http2StreamPriority priority)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var node))
            {
                RegisterStream(streamId, priority);
                return;
            }

            // Remove from old parent
            if (node.ParentId > 0 && _streams.TryGetValue(node.ParentId, out var oldParent))
            {
                oldParent.Children.Remove(streamId);
            }

            node.Priority = priority;

            // Add to new parent
            if (priority.DependsOn > 0)
            {
                UpdateDependency(streamId, priority.DependsOn, priority.Exclusive);
            }
        }
    }

    /// <summary>
    /// Remove stream from priority tree
    /// </summary>
    public void RemoveStream(int streamId)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var node))
                return;

            // Move children to this stream's parent
            foreach (var childId in node.Children.ToList())
            {
                if (_streams.TryGetValue(childId, out var child))
                {
                    child.ParentId = node.ParentId;
                    
                    if (node.ParentId > 0 && _streams.TryGetValue(node.ParentId, out var parent))
                    {
                        parent.Children.Add(childId);
                    }
                }
            }

            // Remove from parent
            if (node.ParentId > 0 && _streams.TryGetValue(node.ParentId, out var oldParent))
            {
                oldParent.Children.Remove(streamId);
            }

            _streams.Remove(streamId);
        }
    }

    /// <summary>
    /// Get next stream ID to process based on priority
    /// Uses weighted round-robin scheduling
    /// </summary>
    public int? GetNextStreamId()
    {
        lock (_lock)
        {
            if (_streams.Count == 0)
                return null;

            // Find stream with highest effective weight
            // Effective weight = weight / (depth in tree)
            var bestStream = _streams.Values
                .OrderByDescending(s => (double)s.Priority.Weight / (GetDepth(s.StreamId) + 1))
                .ThenBy(s => s.StreamId) // Deterministic tie-breaker
                .FirstOrDefault();

            return bestStream?.StreamId;
        }
    }

    private void UpdateDependency(int streamId, int dependsOn, bool exclusive)
    {
        if (dependsOn == streamId)
            return; // Can't depend on self

        if (!_streams.TryGetValue(dependsOn, out var parent))
        {
            // Parent doesn't exist yet, create placeholder
            parent = new StreamPriorityNode
            {
                StreamId = dependsOn,
                Priority = new Http2StreamPriority()
            };
            _streams[dependsOn] = parent;
        }

        if (!_streams.TryGetValue(streamId, out var node))
            return;

        if (exclusive)
        {
            // Move all existing children to be children of this stream
            var existingChildren = parent.Children.ToList();
            parent.Children.Clear();
            
            foreach (var childId in existingChildren)
            {
                if (_streams.TryGetValue(childId, out var child))
                {
                    child.ParentId = streamId;
                    node.Children.Add(childId);
                }
            }
        }

        parent.Children.Add(streamId);
        node.ParentId = dependsOn;
    }

    private int GetDepth(int streamId)
    {
        if (!_streams.TryGetValue(streamId, out var node))
            return 0;

        var depth = 0;
        var currentId = streamId;
        var visited = new HashSet<int>();

        while (currentId > 0)
        {
            if (!visited.Add(currentId))
                break; // Cycle detected

            if (!_streams.TryGetValue(currentId, out var current))
                break;

            currentId = current.ParentId;
            depth++;
        }

        return depth;
    }
}
