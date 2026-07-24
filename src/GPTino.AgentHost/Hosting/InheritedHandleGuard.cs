using System.Runtime.InteropServices;
using System.Text;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Closes disk-file handles this process inherited from its parent at launch.
///
/// The Rhino plug-in spawns the AgentHost with redirected stdout/stderr, which forces
/// <c>bInheritHandles=TRUE</c> on the child. Any inheritable handle the parent (Rhino) holds at
/// that instant then leaks into the long-lived AgentHost — in particular Rhino's read handle on
/// the open <c>.3dm</c>. That leaked handle keeps the document file "in use" for the AgentHost's
/// whole lifetime, so Rhino's save (write temp + rename over the original) fails with
/// "The temporary file could not be renamed."
///
/// The AgentHost never legitimately receives an inherited disk-file handle — it opens its own
/// files non-inheritably after startup — so any inheritable <see cref="FileType.Disk"/> handle
/// present at launch is a leaked parent handle and is safe to close. The inherited stdin/stdout/
/// stderr pipes are <see cref="FileType.Pipe"/> and are never touched. Runs Windows-only, once,
/// before anything else in Main.
/// </summary>
internal static class InheritedHandleGuard
{
    private const uint HandleFlagInherit = 0x00000001;
    private const uint FileTypeDisk = 0x0001;
    // Windows handle values are multiples of four; a freshly launched process holds only a few
    // hundred, so scanning to this bound is well clear of any real table while staying cheap.
    private const int MaxHandleValue = 0x10000;

    /// <summary>Closes every inherited disk-file handle. Returns the paths it closed, for logging.</summary>
    public static IReadOnlyList<string> CloseInheritedDiskHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var closed = new List<string>();
        for (int value = 4; value <= MaxHandleValue; value += 4)
        {
            var handle = (IntPtr)value;
            if (!IsInheritedDiskHandle(handle))
            {
                continue;
            }

            var path = TryGetFinalPath(handle);
            // Closing the inherited handle only drops THIS process's reference; the parent keeps its
            // own. That is exactly the point: the AgentHost's stray reference is what blocks the save.
            if (CloseHandle(handle))
            {
                closed.Add(path ?? $"handle 0x{value:X}");
            }
        }
        return closed;
    }

    /// <summary>
    /// True when the handle is an inheritable disk-file handle — the leaked-parent-handle shape.
    /// The inherited std pipes (FILE_TYPE_PIPE) and console (FILE_TYPE_CHAR) return false, so the
    /// scan never touches the handles the runtime actually relies on.
    /// </summary>
    internal static bool IsInheritedDiskHandle(IntPtr handle)
    {
        if (!GetHandleInformation(handle, out var flags))
        {
            return false;
        }
        if ((flags & HandleFlagInherit) == 0)
        {
            return false;
        }
        return GetFileType(handle) == FileTypeDisk;
    }

    private static string? TryGetFinalPath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            return null;
        }
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            if (GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0) == 0)
            {
                return null;
            }
        }
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
