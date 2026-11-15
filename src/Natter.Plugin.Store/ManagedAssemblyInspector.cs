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
