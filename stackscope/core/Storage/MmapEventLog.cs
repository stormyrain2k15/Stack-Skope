using System.IO.MemoryMappedFiles;

namespace StackScope.Core.Storage;

/// <summary>
/// Append-only memory-mapped event log for a single transaction. Grows in
/// power-of-two chunks so amortised append is O(1). Not thread-safe by
/// itself — <see cref="EventStore"/> owns the lock.
/// </summary>
public sealed class MmapEventLog : IDisposable
{
    private readonly string _path;
    private FileStream _fs;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private long _capacity;
    private long _length;
    private bool _disposed;

    private const long InitialCapacity = 4 * 1024 * 1024;   // 4 MiB
    private const long MaxCapacity     = 64L * 1024 * 1024 * 1024;  // 64 GiB

    public MmapEventLog(string path)
    {
        _path = path;
        _fs = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            options: FileOptions.RandomAccess);

        _length = _fs.Length;
        _capacity = Math.Max(_length, InitialCapacity);
        if (_fs.Length < _capacity)
        {
            _fs.SetLength(_capacity);
        }
        Map();
    }

    public long Length => _length;

    public long Append(ReadOnlySpan<byte> record)
    {
        EnsureCapacity(_length + record.Length);
        // The view accessor doesn't take a Span; use a byte[] pathway.
        // Fast path: write via a rented buffer.
        byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(record.Length);
        try
        {
            record.CopyTo(tmp);
            _view!.WriteArray(_length, tmp, 0, record.Length);
            long offset = _length;
            _length += record.Length;
            return offset;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    public int Read(long offset, Span<byte> dst)
    {
        if (offset + dst.Length > _length)
            throw new ArgumentOutOfRangeException(nameof(dst), "Read past end of log.");
        byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(dst.Length);
        try
        {
            _view!.ReadArray(offset, tmp, 0, dst.Length);
            tmp.AsSpan(0, dst.Length).CopyTo(dst);
            return dst.Length;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    /// <summary>Read a record whose 4-byte length prefix begins at <paramref name="offset"/>.</summary>
    public byte[] ReadRecord(long offset)
    {
        Span<byte> hdr = stackalloc byte[4];
        Read(offset, hdr);
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hdr);
        var buf = new byte[len];
        Read(offset, buf);
        return buf;
    }

    public void Flush()
    {
        _view?.Flush();
        _fs.Flush(true);
    }

    private void EnsureCapacity(long required)
    {
        if (required <= _capacity) return;
        long newCap = _capacity;
        while (newCap < required) newCap *= 2;
        if (newCap > MaxCapacity)
            throw new InvalidOperationException(
                $"Event log would exceed max capacity ({MaxCapacity} bytes).");

        Unmap();
        _fs.SetLength(newCap);
        _capacity = newCap;
        Map();
    }

    private void Map()
    {
        _mmf = MemoryMappedFile.CreateFromFile(
            _fs,
            mapName: null,
            _capacity,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true);
        _view = _mmf.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.ReadWrite);
    }

    private void Unmap()
    {
        _view?.Flush();
        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Unmap();
            // Trim file back to actual length so we don't waste disk.
            _fs.SetLength(_length);
            _fs.Dispose();
        }
        catch { /* best-effort cleanup */ }
    }
}
