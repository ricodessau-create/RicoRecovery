using System.Buffers.Binary;

namespace RicoRecovery.Tests.TestSupport;

internal static class ImageWriter
{
    public static void U16(byte[] image, long offset, long value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)offset), (ushort)value);

    public static void U32(byte[] image, long offset, long value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)offset), (uint)value);

    public static void U64(byte[] image, long offset, long value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan((int)offset), (ulong)value);
}
