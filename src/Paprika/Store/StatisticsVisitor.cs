using HdrHistogram;
using Paprika.Data;

namespace Paprika.Store;

public class StatisticsVisitor : IPageVisitor
{
    private readonly IPageResolver _resolver;

    public readonly Stats State;
    public readonly Stats Ids;
    public readonly Stats Storage;

    public readonly int[] StorageFanOutLevels = new int[StorageFanOut.LevelCount + 1];

    public int AbandonedCount;
    public int TotalVisited;

    private Stats _current;

    public StatisticsVisitor(IPageResolver resolver)
    {
        _resolver = resolver;
        State = new Stats();
        Ids = new Stats();
        Storage = new Stats();

        _current = State;
    }

    public class Stats
    {
        public const int Levels = 32;

        public readonly int[] DataPagePageCountPerNibblePathDepth = new int[Levels];
        public readonly int[] BottomPageCountPerNibblePathDepth = new int[Levels];

        public int BottomLevel = 0;

        /// <summary>
        /// A histogram of used space in inner <see cref="DataPage"/>.
        /// </summary>
        public readonly IntHistogram?[] DataPagePercentageUsed = new IntHistogram[Levels];

        /// <summary>
        /// A histogram of used space in leaf <see cref="BottomPage"/>.
        /// </summary>
        public readonly IntHistogram?[] BottomPagePercentageUsed = new IntHistogram[Levels];

        public void ReportDataPageMap(int length, in SlottedArray map) =>
            ReportMap(DataPagePercentageUsed, length, map);


        public void ReportBottomPageMap(int length, in SlottedArray map) =>
            ReportMap(BottomPagePercentageUsed, length, map);

        private static void ReportMap(IntHistogram?[] histograms, int length, in SlottedArray map)
        {
            var histogram = histograms[length] ??= new IntHistogram(100, 5);
            var percentage = (int)(map.CalculateActualSpaceUsed() * 100);

            histogram.RecordValue(percentage);
        }

        public int PageCount { get; set; }
    }

    public event EventHandler? TotalVisitedChanged;

    public IDisposable On<TPage>(scoped ref NibblePath.Builder prefix, TPage page, DbAddress addr)
        where TPage : unmanaged, IPage
    {
        IncTotalVisited();

        _current.PageCount++;

        var t = typeof(TPage);
        if (t == typeof(StorageFanOut.Level1Page))
        {
            StorageFanOutLevels[1] += 1;
        }
        else if (t == typeof(StorageFanOut.Level2Page))
        {
            StorageFanOutLevels[2] += 1;
        }
        else if (t == typeof(StorageFanOut.Level3Page))
        {
            StorageFanOutLevels[3] += 1;
        }
        else
        {
            var length = prefix.Current.Length;

            var p = page.AsPage();

            switch (p.Header.PageType)
            {
                case PageType.DataPage:
                    _current.DataPagePageCountPerNibblePathDepth[length] += 1;
                    _current.ReportDataPageMap(length, new DataPage(p).Map);
                    break;
                case PageType.Bottom:
                    var bottomLevel = _current.BottomLevel;
                    _current.BottomPageCountPerNibblePathDepth[length + bottomLevel] += 1;
                    _current.ReportBottomPageMap(length, new BottomPage(p).Map);
                    return new BottomLevelUp(_current);
                case PageType.StateRoot:
                    break;
                case PageType.FanOut256:
                    break;
                default:
                    throw new Exception($"Not handled type {p.Header.PageType}");
            }
        }

        return NoopDisposable.Instance;
    }

    private sealed class BottomLevelUp : IDisposable
    {
        private readonly Stats _stats;

        public BottomLevelUp(Stats stats)
        {
            _stats = stats;
            _stats.BottomLevel++;
        }

        public void Dispose()
        {
            _stats.BottomLevel--;
        }
    }

    public IDisposable On<TPage>(TPage page, DbAddress addr) where TPage : unmanaged, IPage
    {
        IncTotalVisited();

        if (typeof(TPage) == typeof(AbandonedPage))
        {
            AbandonedCount += new AbandonedPage(page.AsPage()).CountPages();
        }

        return NoopDisposable.Instance;
    }

    private void IncTotalVisited()
    {
        TotalVisited++;
        TotalVisitedChanged?.Invoke(this, EventArgs.Empty);
    }

    public IDisposable Scope(string name)
    {
        var previous = _current;

        switch (name)
        {
            case StorageFanOut.ScopeIds:
                _current = Ids;
                return new ModeScope(this, previous);
            case StorageFanOut.ScopeStorage:
                _current = Storage;
                return new ModeScope(this, previous);
            case nameof(StorageFanOut):
                return NoopDisposable.Instance;
            default:
                throw new NotImplementedException($"Not implemented for {name}");
        }
    }

    private sealed class ModeScope(StatisticsVisitor visitor, Stats previous) : IDisposable
    {
        public void Dispose() => visitor._current = previous;
    }
}