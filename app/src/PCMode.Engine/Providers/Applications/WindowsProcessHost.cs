using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCMode.Engine;

/// <summary>
/// The real thing: Windows processes, windows and the shell.
/// </summary>
/// <remarks>
/// THE ONLY FILE HERE THAT STARTS ANYTHING
///
/// Starting a program is the one capability this application acquired on purpose,
/// and it is confined to this file and to the command runner beside it -- see the
/// boundary tests, which check that rather than trusting it. Everything above
/// this decides; this is the part that does.
///
/// APPLICATIONS OUTLIVE US, AND MUST
///
/// Nothing started here is owned, waited on, or tied to this process's lifetime.
/// The whole point of the mode is the media player that goes on running after the
/// transition finishes and after the shell is closed; a launcher that cleaned up
/// after itself would close the thing the mode existed to open.
///
/// That is also why this is not the command runner. The two look similar and want
/// opposite things: this must not wait, and that must.
/// </remarks>
public sealed class WindowsProcessHost : IProcessHost
{
    /// <summary>How often a wait looks again.</summary>
    /// <remarks>
    /// Short enough that an application appearing is noticed rather than waited
    /// out, long enough that watching for one costs nothing.
    /// </remarks>
    private static readonly TimeSpan LookAgain = TimeSpan.FromMilliseconds(100);

    public bool IsRunning(string processName) => Find(processName).Length > 0;

    public void Start(LaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Uri is string uri)
        {
            // Handed to the shell, which decides what opens it. Whether anything
            // does was answered before the transition started -- see
            // HandlesUriScheme -- because the shell reports success either way.
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });

            return;
        }

        string executable = command.Executable!;

        var start = new ProcessStartInfo
        {
            FileName = executable,

            // No shell, so nothing parses a command line and nothing can be
            // talked into running a second program by a path with a quote in it.
            // Arguments go through as the separate values they were written as.
            UseShellExecute = false,

            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
        };

        foreach (string argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? started = Process.Start(start);

        // Deliberately not waited for and deliberately not held. See the class
        // remarks: whether it arrived is the readback's question.
    }

    public void Kill(string processName)
    {
        foreach (Process process in Find(processName))
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // It exited between being listed and being ended, or it belongs
                // to somebody else. Either way it is not this call's business:
                // whether the application is gone is what the readback asks, and
                // it is about to.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public bool WaitUntilStopped(string processName, TimeSpan within, CancellationToken cancellationToken) =>
        WaitFor(() => !IsRunning(processName), within, cancellationToken);

    public bool WaitForWindow(string processName, TimeSpan within, CancellationToken cancellationToken) =>
        WaitFor(() => MainWindow(processName) != IntPtr.Zero, within, cancellationToken);

    public bool Activate(string processName)
    {
        IntPtr window = MainWindow(processName);

        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindow(window, SW_RESTORE);
        }

        // Windows refuses to move the foreground for a process the user is not
        // interacting with, and says so by returning false rather than by
        // failing. The caller treats that as "do not type", which is the only
        // safe reading: the keystroke would land in whatever is in front instead.
        return SetForegroundWindow(window);
    }

    public void SendKeys(string processName, string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        IntPtr window = MainWindow(processName);

        if (window == IntPtr.Zero || GetForegroundWindow() != window)
        {
            // Only ever at a window that is actually in front. Synthesised input
            // goes wherever the focus is, so typing without this check is typing
            // at whatever the user happens to be doing.
            return;
        }

        foreach (char character in keys)
        {
            short mapped = VkKeyScanW(character);

            if (mapped == -1)
            {
                // No key on this layout produces that character. Skipped rather
                // than approximated: pressing something else would be sending a
                // command nobody asked for.
                continue;
            }

            var virtualKey = (ushort)(mapped & 0xFF);
            bool shifted = (mapped & 0x100) != 0;

            if (shifted)
            {
                Press(VK_SHIFT, down: true);
            }

            Press(virtualKey, down: true);
            Press(virtualKey, down: false);

            if (shifted)
            {
                Press(VK_SHIFT, down: false);
            }
        }
    }

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public bool HandlesUriScheme(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return false;
        }

        try
        {
            using RegistryKey? handler = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            return handler is not null;
        }
        catch (Exception)
        {
            // A registry that cannot be read must not declare every URI-launched
            // application missing. A false negative here refuses modes that would
            // have worked perfectly, which is worse than the timeout it prevents.
            return true;
        }
    }

    /// <summary>Waits for something to become true, or for the time to run out.</summary>
    private static bool WaitFor(Func<bool> settled, TimeSpan within, CancellationToken cancellationToken)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (settled())
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            try
            {
                Task.Delay(LookAgain, cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static Process[] Find(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The first main window any process of that name has.</summary>
    private static IntPtr MainWindow(string processName)
    {
        Process[] found = Find(processName);

        try
        {
            foreach (Process process in found)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch (Exception)
                {
                    // It exited while being asked. The next one, or none.
                }
            }

            return IntPtr.Zero;
        }
        finally
        {
            foreach (Process process in found)
            {
                process.Dispose();
            }
        }
    }

    private static void Press(ushort virtualKey, bool down)
    {
        INPUT[] input =
        [
            new INPUT
            {
                Type = INPUT_KEYBOARD,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        VirtualKey = virtualKey,
                        Flags = down ? 0 : KEYEVENTF_KEYUP,
                    },
                },
            },
        ];

        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_SHIFT = 0x10;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamL;
        public ushort ParamH;
    }

    /// <summary>
    /// The three shapes of input, overlaid.
    /// </summary>
    /// <remarks>
    /// Only the keyboard one is ever used. The other two are here because the
    /// union has to be the size Windows expects, and a struct that was too small
    /// would have SendInput reject every call.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
