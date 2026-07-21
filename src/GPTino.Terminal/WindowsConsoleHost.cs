using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GPTino.Terminal;

internal static class WindowsConsoleHost
{
    public static void CreateDedicated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!CreateDedicated(FreeConsole, AllocConsole))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not create a visible console for GPTino Terminal.");
        }

        BindStandardStreams();
    }

    internal static bool CreateDedicated(
        Func<bool> releaseConsole,
        Func<bool> allocateConsole)
    {
        ArgumentNullException.ThrowIfNull(releaseConsole);
        ArgumentNullException.ThrowIfNull(allocateConsole);

        _ = releaseConsole();
        return allocateConsole();
    }

    private static void BindStandardStreams()
    {
        var input = new StreamReader(
            OpenConsole("CONIN$", FileAccess.Read),
            Console.InputEncoding,
            detectEncodingFromByteOrderMarks: false);
        var output = new StreamWriter(
            OpenConsole("CONOUT$", FileAccess.Write),
            Console.OutputEncoding)
        {
            AutoFlush = true,
        };
        var error = new StreamWriter(
            OpenConsole("CONOUT$", FileAccess.Write),
            Console.OutputEncoding)
        {
            AutoFlush = true,
        };

        Console.SetIn(input);
        Console.SetOut(output);
        Console.SetError(error);
    }

    private static FileStream OpenConsole(string path, FileAccess access) =>
        new(path, FileMode.Open, access, FileShare.ReadWrite);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
