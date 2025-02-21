namespace Paprika.Store;

public enum PageType : byte
{
    None = 0,

    /// <summary>
    /// <see cref="Store.DataPage"/>.
    /// </summary>
    DataPage = 1,

    /// <summary>
    /// <see cref="StateRootPage"/>
    /// </summary>
    StateRoot = 2,

    /// <summary>
    /// Represents <see cref="AbandonedPage"/>
    /// </summary>
    Abandoned = 3,

    /// <summary>
    /// <see cref="BottomPage"/>
    /// </summary>
    Bottom = 4,

    /// <summary>
    /// <see cref="StorageFanOut"/>
    /// </summary>
    FanOutPage = 5,

    /// <summary>
    /// The fan out page of 256.
    /// </summary>
    FanOut256 = 6,
}