using System.Runtime.InteropServices;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Pins the predicate that decides which handles the AgentHost drops at startup: an inheritable
/// disk-file handle (the shape of Rhino's leaked .3dm handle) yes, a normal non-inheritable file
/// handle no. Windows-only, like the guard itself.
/// </summary>
public sealed class InheritedHandleGuardTests
{
    private const int HANDLE_FLAG_INHERIT = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    [Fact]
    public void InheritableDiskHandleIsSelected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(Path.GetTempPath(), $"gptino-inherit-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            var handle = stream.SafeFileHandle.DangerousGetHandle();

            Assert.False(InheritedHandleGuard.IsInheritedDiskHandle(handle),
                "a freshly opened file handle is not inheritable");

            Assert.True(SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT),
                "marking the handle inheritable should succeed");
            Assert.True(InheritedHandleGuard.IsInheritedDiskHandle(handle),
                "an inheritable disk-file handle is the leaked-parent shape the guard closes");

            // Clearing the flag again must take it back out of scope.
            Assert.True(SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0));
            Assert.False(InheritedHandleGuard.IsInheritedDiskHandle(handle));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void InvalidHandleIsNotSelected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // A value with no open handle behind it must never be treated as closeable.
        Assert.False(InheritedHandleGuard.IsInheritedDiskHandle((IntPtr)0x7FFC));
    }
}
