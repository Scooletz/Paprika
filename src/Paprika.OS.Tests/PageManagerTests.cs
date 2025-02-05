using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Paprika.Tests.OS;

public class PageManagerTests
{
    private string _path;
    private const long Mb8 = 8 * 1024 * 1024;

    [TestCase(true)]
    [TestCase(false)]
    public async Task WritePages(bool continuous)
    {
        const long size = Mb8;

        using var manager = new MemoryMappedPageManager(size, 2, _path, PersistenceOptions.FlushFile);

        const long pageCount = size / Page.PageSize / 4;

        var pages = new DbAddress[pageCount];
        for (uint i = 0; i < pageCount; i++)
        {
            pages[i] = new DbAddress(continuous ? i : i * 2);
        }

        await manager.WritePages(pages, CommitOptions.FlushDataOnly);
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