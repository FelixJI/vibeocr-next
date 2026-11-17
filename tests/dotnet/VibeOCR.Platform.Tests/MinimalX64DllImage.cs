namespace VibeOCR.Platform.Tests;

/// <summary>测试夹具用的最小自洽 Windows x64 DLL PE 头样本。自洽指：COFF
/// 声明的可选头（0xF0）与节表（1×40）都落在实际文件长度内；仅头部/声明
/// 范围语义，不代表可运行映像。</summary>
internal static class MinimalX64DllImage
{
    internal const ushort Amd64 = 0x8664;
    internal const ushort I386 = 0x014C;
    internal const ushort DefaultCharacteristics = 0x2022; // EXECUTABLE_IMAGE|LARGE_ADDRESS_AWARE|DLL
    internal const ushort Pe32Plus = 0x20B;
    internal const ushort Pe32 = 0x10B;
    // 覆盖 NT 头(24) + 可选头(0xF0) + 1 个节头(40)，自洽声明范围。
    internal const int SelfConsistentLength = 0x80 + 24 + 0xF0 + 40;

    internal static byte[] Create(
        ushort machine = Amd64,
        ushort characteristics = DefaultCharacteristics,
        ushort optionalHeaderMagic = Pe32Plus,
        int peOffset = 0x80,
        int length = SelfConsistentLength)
    {
        byte[] image = new byte[length];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteInt32(image, 0x3C, peOffset);
        image[peOffset + 0] = (byte)'P';
        image[peOffset + 1] = (byte)'E';
        WriteUInt16(image, peOffset + 4, machine);
        WriteUInt16(image, peOffset + 6, 1);          // NumberOfSections
        WriteUInt16(image, peOffset + 20, 0xF0);      // SizeOfOptionalHeader
        WriteUInt16(image, peOffset + 22, characteristics);
        WriteUInt16(image, peOffset + 24, optionalHeaderMagic);
        return image;
    }

    private static void WriteUInt16(byte[] image, int offset, ushort value)
    {
        image[offset] = (byte)(value & 0xFF);
        image[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] image, int offset, int value)
    {
        for (int index = 0; index < 4; index++)
        {
            image[offset + index] = (byte)(value >> (8 * index));
        }
    }
}
