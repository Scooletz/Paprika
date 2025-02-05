using System.Runtime.InteropServices;

namespace Paprika.Tests.OS;

/// <summary>
/// A dummy suite testing execution on different systems.
/// </summary>
public class OsTests
{
    [Test]
    public void Test()
    {
        Console.WriteLine(RuntimeInformation.OSDescription);
    }
}