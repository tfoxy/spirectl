using System.Text;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class GodotPackedResourceCatalog
{
    private const uint PackHeaderMagic = 0x43504447;
    private const uint PackFormatVersionV2 = 2;
    private const uint PackFormatVersionV3 = 3;
    private const uint PackFormatVersionV4 = 4;

    private const uint PackDirEncrypted = 1 << 0;
    private const uint PackRelFilebase = 1 << 1;
    private const uint PackSparseBundle = 1 << 2;

    private const uint PackFileEncrypted = 1 << 0;
    private const uint PackFileRemoval = 1 << 1;
    private const uint PackFileDelta = 1 << 2;

    public PackedCatalog Load(string packPath)
    {
        using var stream = File.OpenRead(packPath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != PackHeaderMagic)
        {
            return new PackedCatalog([], [$"Skipped packed resource '{packPath}' because it did not start with a Godot PCK header."]);
        }

        var version = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        if (version is not (PackFormatVersionV2 or PackFormatVersionV3 or PackFormatVersionV4))
        {
            return new PackedCatalog([], [$"Skipped packed resource '{packPath}' because pack format version {version} is unsupported."]);
        }

        var packFlags = reader.ReadUInt32();
        var encryptedDirectory = (packFlags & PackDirEncrypted) != 0;
        var sparseBundle = (packFlags & PackSparseBundle) != 0;
        var fileBase = reader.ReadUInt64();

        ulong directoryOffset;
        if (version is PackFormatVersionV3 or PackFormatVersionV4)
        {
            directoryOffset = reader.ReadUInt64();

            if (encryptedDirectory && version == PackFormatVersionV4 && sparseBundle)
            {
                _ = reader.ReadBytes(32);
            }

            reader.BaseStream.Seek((long)directoryOffset, SeekOrigin.Begin);
        }
        else
        {
            for (var i = 0; i < 16; i++)
            {
                _ = reader.ReadUInt32();
            }
        }

        if (encryptedDirectory)
        {
            return new PackedCatalog([], [$"Skipped packed resource '{packPath}' because encrypted PCK directories are unsupported."]);
        }

        var fileCount = reader.ReadInt32();
        var entries = new List<PackedEntry>(fileCount);
        var notes = new List<string>();

        for (var i = 0; i < fileCount; i++)
        {
            var pathLength = reader.ReadUInt32();
            var logicalPath = DecodeUtf8(reader.ReadBytes((int)pathLength));
            var offset = reader.ReadUInt64();
            var size = reader.ReadUInt64();
            _ = reader.ReadBytes(16);
            var fileFlags = reader.ReadUInt32();

            if ((fileFlags & PackFileRemoval) != 0)
            {
                continue;
            }

            if ((fileFlags & PackFileEncrypted) != 0 || (fileFlags & PackFileDelta) != 0 || sparseBundle)
            {
                notes.Add($"Skipped packed entry '{NormalizeLogicalPath(logicalPath)}' from '{packPath}' because encrypted, delta, or sparse bundled files are unsupported.");
                continue;
            }

            entries.Add(new PackedEntry(
                NormalizeLogicalPath(logicalPath),
                packPath,
                offset + fileBase,
                size));
        }

        return new PackedCatalog(entries, notes);
    }

    public byte[] ReadEntryBytes(PackedEntry entry)
    {
        using var stream = File.OpenRead(entry.PackPath);
        stream.Seek((long)entry.Offset, SeekOrigin.Begin);
        var buffer = new byte[entry.Size];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);
        if (bytesRead != buffer.Length)
        {
            throw new IOException($"Expected {buffer.Length} bytes for packed entry '{entry.LogicalPath}' but only read {bytesRead}.");
        }

        return buffer;
    }

    private static string NormalizeLogicalPath(string path)
    {
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return path.Replace('\\', '/');
        }

        return $"res://{path.TrimStart('/').Replace('\\', '/')}";
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes[^1] == 0)
        {
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal sealed record PackedCatalog(
        IReadOnlyList<PackedEntry> Entries,
        IReadOnlyList<string> Notes);

    internal sealed record PackedEntry(
        string LogicalPath,
        string PackPath,
        ulong Offset,
        ulong Size);
}
