namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// HPACK dynamic table for header compression
/// </summary>
public class HpackDynamicTable
{
    private readonly List<(string name, string value)> _entries = new();
    private int _maxSize;
    private int _currentSize;
    
    public HpackDynamicTable(int maxSize)
    {
        _maxSize = maxSize;
    }
    
    public void Add(string name, string value)
    {
        var entrySize = 32 + name.Length + value.Length; // RFC 7541 4.1
        
        // Evict entries if necessary
        while (_currentSize + entrySize > _maxSize && _entries.Count > 0)
        {
            var last = _entries[^1];
            _currentSize -= 32 + last.name.Length + last.value.Length;
            _entries.RemoveAt(_entries.Count - 1);
        }
        
        if (entrySize <= _maxSize)
        {
            _entries.Insert(0, (name, value));
            _currentSize += entrySize;
        }
    }
    
    public (string name, string value) Get(int index)
    {
        if (index >= 0 && index < _entries.Count)
            return _entries[index];
        
        throw new ArgumentOutOfRangeException(nameof(index));
    }
    
    public void UpdateMaxSize(int newMaxSize)
    {
        _maxSize = newMaxSize;
        
        // Evict entries if new size is smaller
        while (_currentSize > _maxSize && _entries.Count > 0)
        {
            var last = _entries[^1];
            _currentSize -= 32 + last.name.Length + last.value.Length;
            _entries.RemoveAt(_entries.Count - 1);
        }
    }
}
