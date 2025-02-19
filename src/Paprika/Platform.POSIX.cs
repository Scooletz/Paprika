using System.Runtime.InteropServices;

namespace Paprika;


public static partial class Platform
{
    private sealed class PosixMemoryManager : IMemoryManager
    {
        [Flags]
        private enum Advice : int
        {
            // ReSharper disable InconsistentNaming
            /// <summary>
            ///  Expect access in the near future. (Hence, it might be a good idea to read some pages ahead.)
            /// </summary>
            MADV_WILLNEED = 0x3,

            /// <summary>
            ///  Do not expect access in the near future.
            /// (For the time being, the application is finished with the given range,
            /// so the kernel can free resources associated with it.)
            /// </summary>
            MADV_DONTNEED = 0x4,
            // ReSharper restore InconsistentNaming
        }

        [DllImport("LIBC_6", SetLastError = true)]
        static extern int madvise(IntPtr addr, UIntPtr length, Advice advice);

        // For Linux
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr __errno_location();

        // For macOS
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr __error();

        private static int GetErrno()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Marshal.ReadInt32(__errno_location());
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Marshal.ReadInt32(__error());
            }

            throw new PlatformNotSupportedException("This platform is not supported.");
        }

        public void SchedulePrefetch(ReadOnlySpan<AddressRange> ranges)
        {
            const int success = 0;

            foreach (var t in ranges)
            {
                var result = madvise((IntPtr)t.Pointer, t.Length, Advice.MADV_WILLNEED);
                if (result != success)
                {
                    throw new SystemException($"{nameof(madvise)} failed with the following error: {GetErrno()}");
                }
            }
        }
    }
}