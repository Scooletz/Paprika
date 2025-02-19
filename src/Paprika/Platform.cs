using System.Runtime.InteropServices;

namespace Paprika;

public static partial class Platform
{
    public static bool CanPrefetch => Manager != null;

    /// <summary>
    /// Schedules an OS dependent prefetch. Performs a sys-call and should not be called from the main thread.
    /// </summary>
    /// <param name="ranges"></param>
    public static void Prefetch(ReadOnlySpan<AddressRange> ranges) => Manager!.SchedulePrefetch(ranges);

    private static readonly IMemoryManager? Manager = CreateManager();

    private static IMemoryManager? CreateManager()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsMemoryManager();

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new PosixMemoryManager() : null;
    }


    private interface IMemoryManager
    {
        /// <summary>
        /// Schedules an OS dependent prefetch. Performs a sys-call and should not be called from the main thread.
        /// </summary>
        void SchedulePrefetch(ReadOnlySpan<AddressRange> addresses);
    }
}

/// <summary>
/// Describes an unmanaged range.
/// </summary>
/// <param name="Pointer">The pointer to prefetch.</param>
/// <param name="Length">The length to prefetch.</param>
public record struct AddressRange(UIntPtr Pointer, uint Length);