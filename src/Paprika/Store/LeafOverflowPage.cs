﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// The page used to store big chunks of data.
/// </summary>
[method: DebuggerStepThrough]
public readonly unsafe struct LeafOverflowPage(Page page) : IPage
{
    private ref PageHeader Header => ref page.Header;

    private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

    [InlineArray(Size)]
    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    private struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;

        /// <summary>
        /// The first item of map of frames to allow ref to it.
        /// </summary>
        private byte DataStart;

        /// <summary>
        /// Writable area.
        /// </summary>
        public Span<byte> DataSpan => MemoryMarshal.CreateSpan(ref DataStart, Size);
    }

    public SlottedArray Map => new(Data.DataSpan);

    public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
    {
        using var scope = visitor.On(ref builder, this, addr);
    }

    public void Report(IReporter reporter, IPageResolver resolver, int level, int trimmedNibbles)
    {
        reporter.ReportDataUsage(Header.PageType, level, trimmedNibbles, Map);
    }
}