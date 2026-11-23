using System;
using System.Runtime.InteropServices;

namespace WinDiskMap.Tui
{
    /// <summary>Включение VT/ANSI-последовательностей в стандартной консоли Windows.</summary>
    internal static class Vt
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        public static bool Enable()
        {
            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                uint mode;
                if (handle != IntPtr.Zero && GetConsoleMode(handle, out mode))
                    return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
