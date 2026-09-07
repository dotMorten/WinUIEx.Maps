using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Win32.Graphics.Direct3D11;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class DirectXGeometryStreamingTests
{
    [TestMethod]
    public unsafe void AppendMapsNoOverwriteAndCopiesOnlyTheReservedBytes()
    {
        IntPtr* vtable = stackalloc IntPtr[16];
        InitializeVtable(vtable);
        byte* storage = stackalloc byte[12];
        new Span<byte>(storage, 12).Fill(0xcc);
        TestContext context = new() { Vtable = vtable, Data = storage };
        byte* first = stackalloc byte[] { 1, 2, 3 };
        byte* second = stackalloc byte[] { 4, 5, 6 };

        WriteDynamicVertexBuffer((IntPtr)(&context), (IntPtr)1, first, 3, 12, 0, true);
        Assert.AreEqual(D3D11_MAP.D3D11_MAP_WRITE_DISCARD, context.LastMap);
        WriteDynamicVertexBuffer((IntPtr)(&context), (IntPtr)1, second, 3, 12, 3, false);

        Assert.AreEqual(D3D11_MAP.D3D11_MAP_WRITE_NO_OVERWRITE, context.LastMap);
        Assert.AreSequenceEqual(
            new byte[] { 1, 2, 3, 4, 5, 6, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc },
            new Span<byte>(storage, 12).ToArray());
        Assert.AreEqual(2, context.MapCount);
        Assert.AreEqual(2, context.UnmapCount);
    }

    [TestMethod]
    public unsafe void LegacyDiscardHelperStillMapsAndWritesAtZero()
    {
        IntPtr* vtable = stackalloc IntPtr[16];
        InitializeVtable(vtable);
        byte* storage = stackalloc byte[3];
        TestContext context = new() { Vtable = vtable, Data = storage };
        byte* source = stackalloc byte[] { 7, 8, 9 };

        WriteDiscardBuffer((IntPtr)(&context), (IntPtr)1, source, 3);

        Assert.AreEqual(D3D11_MAP.D3D11_MAP_WRITE_DISCARD, context.LastMap);
        Assert.AreSequenceEqual(new byte[] { 7, 8, 9 }, new Span<byte>(storage, 3).ToArray());
        Assert.AreEqual(1, context.UnmapCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public unsafe void FailedMapThrowsWithoutCopyingOrUnmapping(bool discard)
    {
        IntPtr* vtable = stackalloc IntPtr[16];
        InitializeVtable(vtable);
        byte* storage = stackalloc byte[3];
        new Span<byte>(storage, 3).Fill(0xcc);
        TestContext context = new()
        {
            Vtable = vtable,
            Data = storage,
            MapResult = unchecked((int)0x80004005),
        };
        IntPtr contextPointer = (IntPtr)(&context);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WriteDynamicVertexBuffer(contextPointer, (IntPtr)1, null, 3, 3, 0, discard));

        Assert.AreEqual(1, context.MapCount);
        Assert.AreEqual(0, context.UnmapCount);
        Assert.AreSequenceEqual(new byte[] { 0xcc, 0xcc, 0xcc }, new Span<byte>(storage, 3).ToArray());
    }

    [TestMethod]
    [DataRow(0, 12, 0)]
    [DataRow(6, 12, 9)]
    [DataRow(3, 12, 15)]
    public unsafe void InvalidByteRangesAreRejectedBeforeNativeCalls(int count, int capacity, int offset) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WriteDynamicVertexBuffer(
                IntPtr.Zero, IntPtr.Zero, null, (nuint)count, (nuint)capacity, (nuint)offset, false));

    [TestMethod]
    public unsafe void OverflowingByteRangesAndNonzeroDiscardOffsetsAreRejectedBeforeNativeCalls()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WriteDynamicVertexBuffer(IntPtr.Zero, IntPtr.Zero, null, nuint.MaxValue, 12, 3, false));
        Assert.ThrowsExactly<ArgumentException>(() =>
            WriteDynamicVertexBuffer(IntPtr.Zero, IntPtr.Zero, null, 3, 12, 3, true));
    }

    [TestMethod]
    public unsafe void DrawVerticesUsesRequestedStartAndPreservesDefaultZero()
    {
        IntPtr* vtable = stackalloc IntPtr[16];
        InitializeVtable(vtable);
        TestContext context = new() { Vtable = vtable };

        DrawVertices((IntPtr)(&context), 6, 12);
        Assert.AreEqual(6u, context.DrawCount);
        Assert.AreEqual(12u, context.DrawStart);
        DrawVertices((IntPtr)(&context), 3);
        Assert.AreEqual(3u, context.DrawCount);
        Assert.AreEqual(0u, context.DrawStart);
    }

    private static unsafe void InitializeVtable(IntPtr* vtable)
    {
        vtable[13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)&Draw;
        vtable[14] = (IntPtr)(delegate* unmanaged[Stdcall]<
            IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)&Map;
        vtable[15] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)&Unmap;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int Map(
        IntPtr pointer,
        IntPtr resource,
        uint subresource,
        D3D11_MAP map,
        uint flags,
        D3D11_MAPPED_SUBRESOURCE* mapped)
    {
        TestContext* context = (TestContext*)pointer;
        context->MapCount++;
        context->LastMap = map;
        mapped->pData = context->Data;
        return context->MapResult;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void Unmap(IntPtr pointer, IntPtr resource, uint subresource) =>
        ((TestContext*)pointer)->UnmapCount++;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void Draw(IntPtr pointer, uint count, uint start)
    {
        TestContext* context = (TestContext*)pointer;
        context->DrawCount = count;
        context->DrawStart = start;
    }

    private unsafe struct TestContext
    {
        internal IntPtr* Vtable;
        internal byte* Data;
        internal D3D11_MAP LastMap;
        internal int MapCount;
        internal int UnmapCount;
        internal int MapResult;
        internal uint DrawCount;
        internal uint DrawStart;
    }
}
