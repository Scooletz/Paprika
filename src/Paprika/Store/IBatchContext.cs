using System.Diagnostics;
using System.Runtime.CompilerServices;
using Paprika.Crypto;

namespace Paprika.Store;

public interface IBatchContext : IReadOnlyBatchContext
{
    /// <summary>
    /// Get the address of the given page.
    /// </summary>
    DbAddress GetAddress(Page page);

    /// <summary>
    /// Gets a new (potentially reused) page. If <paramref name="clear"/> is set, the page will be cleared. 
    /// </summary>
    /// <returns>A new page.</returns>
    Page GetNewPage(out DbAddress addr, bool clear);

    TPage GetNewPage<TPage>(out DbAddress addr, byte level = 0)
        where TPage : struct, IPage<TPage>
    {
        var page = GetNewPage(out addr, false);
        var wrapped = TPage.Wrap(page);
        wrapped.Clear();

        page.Header.PageType = TPage.DefaultType;
        page.Header.Level = level;
        return wrapped;
    }

    /// <summary>
    /// Gets a new (potentially reused) page that is clean and ready to be used.
    /// </summary>
    /// <param name="addr">The address of the page that is returned.</param>
    /// <param name="level">The level to be assigned to.</param>
    /// <typeparam name="TPage"></typeparam>
    /// <returns>The typed page.</returns>
    TPage GetNewCleanPage<TPage>(out DbAddress addr, byte level = 0)
        where TPage : struct, IPage<TPage>, IClearable
    {
        var page = GetNewPage(out addr, false);

        page.Header.PageType = TPage.DefaultType;
        page.Header.Level = level;

        var result = TPage.Wrap(page);
        result.Clear();

        return result;
    }

    /// <summary>
    /// Gets a writable copy of the page.
    /// </summary>
    /// <param name="page"></param>
    /// <returns></returns>
    Page GetWritableCopy(Page page);

    Page EnsureWritableCopy(ref DbAddress addr)
    {
        Debug.Assert(addr.IsNull == false);

        var page = GetAt(addr);

        if (page.Header.BatchId == BatchId)
        {
            return page;
        }

        var cow = GetWritableCopy(page);
        addr = GetAddress(cow);
        return cow;
    }

    /// <summary>
    /// Informs the batch, that the given page was abandoned before and is manually reused.
    /// </summary>
    /// <param name="page">The page that will be reused.</param>
    public void NoticeAbandonedPageReused(Page page);

    /// <summary>
    /// Checks whether the page was written during this batch.
    /// </summary>
    bool WasWritten(DbAddress addr);

    /// <summary>
    /// Abandon this page from this batch on.
    /// </summary>
    /// <param name="page">The page to be reused.</param>
    /// <param name="possibleImmediateReuse">If set to true, the page will be checked if it was written in this batch and if it was, will be reused immediately.</param>
    void RegisterForFutureReuse(Page page, bool possibleImmediateReuse = false);

    /// <summary>
    /// Assigns the batch identifier to a given page, marking it writable by this batch.
    /// </summary>
    void AssignBatchId(Page page);

    /// <summary>
    /// Tries to get the page and if it does not exist, allocates one.
    /// </summary>
    /// <param name="addr">The address to check.</param>
    /// <param name="pageType">The page type to assign.</param>
    /// <returns>The page either allocated or get.</returns>
    Page TryGetPageAlloc(ref DbAddress addr, PageType pageType);
}

public interface IReadOnlyBatchContext : IPageResolver
{
    /// <summary>
    /// Gets the current <see cref="IBatch"/> id.
    /// </summary>
    uint BatchId { get; }

    IDictionary<Keccak, uint> IdCache { get; }
}

/// <summary>
/// Provides a capability to resolve a page by its page address.
/// </summary>
public interface IPageResolver
{
    /// <summary>
    /// Gets the page at given address.
    /// </summary>
    Page GetAt(DbAddress address);

    /// <summary>
    /// Issues a prefetch request for the page at the specific location <paramref name="address"/>
    /// using mechanism defined by the <paramref name="mode"/>.
    /// </summary>
    void Prefetch(DbAddress address, PrefetchMode mode);

    /// <summary>
    /// Issues a prefetch request for a set of pages residing at <paramref name="addresses"/>.
    /// The prefetch mode that is used is <see cref="PrefetchMode.Heavy"/>.
    /// </summary>
    void Prefetch(ReadOnlySpan<DbAddress> addresses);

    [SkipLocalsInit]
    void Prefetch<TAddressList>(in TAddressList addresses)
        where TAddressList : struct, DbAddressList.IDbAddressList
    {
        Span<DbAddress> span = stackalloc DbAddress[TAddressList.Length];

        // Copy all
        for (var i = 0; i < TAddressList.Length; i++)
        {
            span[i] = addresses[i];
        }

        Prefetch(span);
    }
}

public enum PrefetchMode
{
    /// <summary>
    /// Expects that the page was not evicted and only should be brought to CPU case using SSE prefetch.
    /// </summary>
    Soft,

    /// <summary>
    /// Expects that the page was not accessed lately or was evicted from the memory.
    /// The page should be prefetched using platform specific heavy prefetch <see cref="Platform.Prefetch"/>.
    /// </summary>
    Heavy,
}
