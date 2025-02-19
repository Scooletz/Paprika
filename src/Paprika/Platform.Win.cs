using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika;


public static partial class Platform
{
    /// <summary>
    /// The windows memory manager.
    /// </summary>
    private sealed class WindowsMemoryManager : IMemoryManager
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool PrefetchVirtualMemory(IntPtr hProcess, ulong numberOfEntries,
            Win32MemoryRangeEntry* entries, int flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32MemoryRangeEntry
        {
            /// <summary>
            /// Starting address of the memory range
            /// </summary>
            public IntPtr VirtualAddress;

            /// <summary>
            /// Size of the memory range in bytes
            /// </summary>
            public UIntPtr NumberOfBytes;
        }

        [SkipLocalsInit]
        public unsafe void SchedulePrefetch(ReadOnlySpan<AddressRange> ranges)
        {
            var count = ranges.Length;
            var ptr = stackalloc Win32MemoryRangeEntry[count];
            var span = new Span<Win32MemoryRangeEntry>(ptr, count);

            for (var i = 0; i < span.Length; i++)
            {
                span[i].VirtualAddress = (IntPtr)ranges[i].Pointer;
                span[i].NumberOfBytes = ranges[i].Length;
            }

            const int reserved = 0;

            if (PrefetchVirtualMemory(Process.GetCurrentProcess().Handle, (ulong)count, ptr, reserved) == false)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}