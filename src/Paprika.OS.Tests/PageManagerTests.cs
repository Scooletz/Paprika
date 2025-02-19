using FluentAssertions;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Paprika.OS.Tests;

public class PageManagerTests
{
    private string _path;
    private const long Mb8 = 8 * 1024 * 1024;

    [TestCase(true)]
    [TestCase(false)]
    public async Task WritePages(bool continuous)
    {
        using var manager = new MemoryMappedPageManager(Mb8, 2, _path, PersistenceOptions.FlushFile);

        const long pageCount = Mb8 / Page.PageSize / 4;

        var pages = new DbAddress[pageCount];
        for (uint i = 0; i < pageCount; i++)
        {
            pages[i] = new DbAddress(continuous ? i : i * 2);
        }

        await manager.WritePages(pages, CommitOptions.FlushDataOnly);
    }

    [Platform("Linux")]
    [Platform("Win")]
    public void CanPrefetch()
    {
        using var manager = new MemoryMappedPageManager(Mb8, 2, _path, PersistenceOptions.FlushFile);

        Platform.CanPrefetch.Should().BeTrue();

        Platform.Prefetch([manager.GetAddressRange(new DbAddress(13u))]);
    }

    [SetUp]
    public void SetUp()
    {
        var ctx = TestContext.CurrentContext;
        _path = Path.Combine(ctx.TestDirectory, ctx.Test.Name);

        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }

        Directory.CreateDirectory(_path);
    }
}