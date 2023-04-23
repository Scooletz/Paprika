﻿using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika.Pages.Frames;

/// <summary>
/// Provides a wrapping for a <see cref="byte"/> based index of a <see cref="IFrame"/> on the page,
/// so that 0 can be used as a value and is different from the null value. 
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public readonly struct Frame
{
    public const int Size = 8;

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Pool
    {
        public const int Size = 2;

        [FieldOffset(0)] private byte NextFree;
        [FieldOffset(1)] private FrameIndex Released;

        /// <summary>
        /// Releases the given frame adding it to the released set.
        /// </summary>
        public void Release(ref Frame frame, Span<Frame> frames)
        {
            ref var header = ref Unsafe.As<Frame, Header>(ref frame);

            // point to the current released
            header.Next = Released;

            var index = GetIndex(frames, ref frame);

            // set to the frame
            Released = FrameIndex.FromIndex(index);
        }

        private static byte GetIndex(in Span<Frame> frames, ref Frame frame)
        {
            return (byte)(Unsafe.ByteOffset(ref frames[0], ref frame).ToInt64() / Frame.Size);
        }

        /// <summary>
        /// Tries to write payload as a new frame.
        /// </summary>
        public bool TryWrite(Span<byte> payload, FrameIndex next, Span<Frame> frames, out FrameIndex writtenTo)
        {
            var frameCount = GetFrameCount(payload);

            // try use free first
            if (NextFree + frameCount <= frames.Length)
            {
                Write(ref frames[NextFree], payload, next);
                writtenTo = FrameIndex.FromIndex(NextFree);

                NextFree += frameCount;
                return true;
            }

            // no Released, no space
            if (Released.IsNull)
            {
                writtenTo = default;
                return false;
            }

            ref var frame = ref frames[Released.Value];

            // check header
            ref var header = ref Unsafe.As<Frame, Header>(ref frame);
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Reads a <see cref="Span{Byte}"/> from the given frame pointed by <paramref name="frame"/>
    /// </summary>
    /// <returns>Byte representation.</returns>
    public static Span<byte> Read(ref Frame frame, out FrameIndex next)
    {
        var header = Unsafe.As<Frame, Header>(ref frame);
        next = header.Next;

        return CreateByteSpan(ref frame, header);
    }

    /// <summary>
    /// Writes to the given <paramref name="frame"/> a payload of <paramref name="bytes"/>.
    /// </summary>
    /// <param name="frame"></param>
    /// <param name="bytes"></param>
    /// <param name="next">The next to point to.</param>
    private static void Write(ref Frame frame, Span<byte> bytes, FrameIndex next)
    {
        var length = GetFrameCount(bytes);

        ref var header = ref Unsafe.As<Frame, Header>(ref frame);

        header.Length = length;
        header.Next = next;

        var dest = CreateByteSpan(ref frame, header);
        bytes.CopyTo(dest);
    }

    private static Span<byte> CreateByteSpan(ref Frame frame, Header header)
    {
        ref var bytes = ref Unsafe.Add(ref Unsafe.As<Frame, byte>(ref frame), Header.Size);
        return MemoryMarshal.CreateSpan(ref bytes, header.Length * Size - Header.Size);
    }

    /// <summary>
    /// Gets the number of frames needed to store <see cref="bytes"/> payload.
    /// </summary>
    private static byte GetFrameCount(Span<byte> bytes) => (byte)AlignToFrames(bytes.Length + Header.Size);

    private static int AlignToFrames(int length) => ((length + (Size - 1)) & -Size) / Size;

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Header
    {
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public const int Size = 2;

        [FieldOffset(0)] public byte Length;

        [FieldOffset(1)] public FrameIndex Next;
    }
}