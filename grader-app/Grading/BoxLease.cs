namespace grader_app.Grading;

public sealed class BoxLease : IDisposable
{
    private readonly FileStream _lock;

    private BoxLease(FileStream stream) => _lock = stream;

    public static BoxLease Acquire(uint boxId)
    {
        // Keep the inode after release: unlinking it would allow two owners to lock different inodes.
        var path = Path.Combine(Path.GetTempPath(), $"valimark-box-{boxId}.lock");
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Box lock path cannot be a symbolic link.");
        try
        {
            return new BoxLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException($"Box {boxId} is busy or its lock is inaccessible. Choose another --box-id. " +
                                  "All grader processes must use the same TMPDIR and judge account.", exception);
        }
    }

    public void Dispose() => _lock.Dispose();
}
