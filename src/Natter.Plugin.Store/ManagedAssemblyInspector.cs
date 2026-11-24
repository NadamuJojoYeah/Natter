namespace Natter.Plugin.Store;

/// <summary>Rejects native/hybrid binaries in a managed plugin package.</summary>
internal static class ManagedAssemblyInspector
{
    private const int PeHeaderOffset = 0x3C;
    private const ushort DosSignature = 0x5A4D; // MZ
    private const uint PeSignature = 0x00004550; // PE\0\0
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int CliHeaderDirectoryIndex = 14;

        /// <summary>
    /// Magic-byte detection for native images, independent of the file extension (S2-C13). A native
    /// library renamed to <c>libfoo.dll</c>/<c>data.bin</c> must still be rejected when
    /// <c>AllowNativeLibraries</c> is false.
    /// </summary>
    public static bool LooksLikeNativeImage(Stream stream)
    {
        if (stream.Length < 4)
            return false;

        try
        {
            var header = new byte[4];
            stream.Position = 0;
            if (stream.Read(header, 0, 4) != 4)
                return false;

            // ELF: 0x7F 'E' 'L' 'F'
            if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
                return true;

            // Mach-O: 0xFEEDFACE / 0xFEEDFACF (both endianness) and FAT 0xCAFEBABE / 0xBEBAFECA.
            if (IsMachOMagic(header))
                return true;

            // PE that is not managed: the CLI header check below catches it (MZ + PE without CLI).
            stream.Position = 0;
            return !IsManagedAssembly(stream) && IsPortableExecutable(stream);
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static bool IsMachOMagic(byte[] header)
    {
        uint bigEndian = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
        uint littleEndian = (uint)(header[3] << 24 | header[2] << 16 | header[1] << 8 | header[0]);

        return bigEndian is 0xFEEDFACE or 0xFEEDFACF or 0xCAFEBABE or 0xCEFAEDFE
            || littleEndian is 0xFEEDFACE or 0xFEEDFACF;
    }

    private static bool IsPortableExecutable(Stream stream)
    {
        if (stream.Length < 0x40)
            return false;

        stream.Position = 0;
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            return false;

        stream.Position = PeHeaderOffset;
        var buffer = new byte[4];
        if (stream.Read(buffer, 0, 4) != 4)
            return false;

        var peOffset = buffer[0] | buffer[1] << 8 | buffer[2] << 16 | buffer[3] << 24;
        if (peOffset <= 0 || peOffset + 4 > stream.Length)
            return false;

        stream.Position = peOffset;
        return stream.Read(buffer, 0, 4) == 4
               && buffer[0] == (byte)'P' && buffer[1] == (byte)'E' && buffer[2] == 0 && buffer[3] == 0;
    }

    public static bool IsManagedAssembly(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 0x40)
                return false;

            stream.Position = 0;
            if (reader.ReadUInt16() != DosSignature)
                return false;

            stream.Position = PeHeaderOffset;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24 > stream.Length)
                return false;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != PeSignature)
                return false;

            stream.Position = peOffset + 24; // PE signature (4) + COFF header (20)
            var optionalMagic = reader.ReadUInt16();
            var optionalHeaderStart = peOffset + 24;
            var dataDirectoryOffset = optionalMagic switch
            {
                Pe32Magic => optionalHeaderStart + 96,
                Pe32PlusMagic => optionalHeaderStart + 112,
                _ => -1,
            };

            if (dataDirectoryOffset < 0)
                return false;

            var cliDirectoryOffset = dataDirectoryOffset + CliHeaderDirectoryIndex * 8;
            if (cliDirectoryOffset + 8 > stream.Length)
                return false;

            stream.Position = cliDirectoryOffset;
            var cliRva = reader.ReadUInt32();
            var cliSize = reader.ReadUInt32();
            return cliRva != 0 && cliSize != 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return false;
        }
    }
}
