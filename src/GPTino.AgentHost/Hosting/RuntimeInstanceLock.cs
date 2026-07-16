using System.Text;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Holds the process-wide ownership of one file-pair data root. File sharing is
/// enforced by the operating system and released automatically if the process exits.
/// </summary>
public sealed class RuntimeInstanceLock : IDisposable
{
    private const string LockFileName = ".gptino-instance.lock";

    private FileStream? _stream;

    private RuntimeInstanceLock(FileStream stream, string dataDirectory)
    {
        _stream = stream;
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public static RuntimeInstanceLock Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var canonicalDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(canonicalDirectory);
        var lockPath = Path.Combine(canonicalDirectory, LockFileName);
        RejectReparsePointAncestors(canonicalDirectory);
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            try
            {
                if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "GPTino runtime lock must be a regular file, not a reparse point.");
                }
                var owner = Encoding.UTF8.GetBytes(
                    $"pid={Environment.ProcessId}\nstartedUtc={DateTimeOffset.UtcNow:O}\n");
                stream.SetLength(0);
                stream.Write(owner);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                return new RuntimeInstanceLock(stream, canonicalDirectory);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another GPTino AgentHost already owns this Rhino/Grasshopper file pair. " +
                $"Close that runtime before opening a second one. Data directory: {canonicalDirectory}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"GPTino cannot acquire exclusive ownership of its data directory: {canonicalDirectory}",
                exception);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private static void RejectReparsePointAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"GPTino runtime data path crosses a reparse point: {current.FullName}");
            }
        }
    }
}
