using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WSGM.DeviceLab.Inventory;

internal static class NativePeInspector
{
    private const int MaximumExportNameBytes = 1024;

    public static NativeBinaryInventory Inspect(string path)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            return Unavailable(path, InventoryAccess.Malformed);
        }

        try
        {
            using FileStream stream = new(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > InventoryLimits.MaximumNativeBinaryBytes)
            {
                return Unavailable(resolved, InventoryAccess.Malformed, stream.Length);
            }
            using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.PEHeader is null)
            {
                return Unavailable(resolved, InventoryAccess.Malformed, stream.Length);
            }

            (BinarySignatureState signature, string? signer) = ReadSigner(resolved);
            IReadOnlyList<string> exports = ReadExports(pe);
            string sha256 = Hash(stream);
            return new NativeBinaryInventory
            {
                Access = InventoryAccess.Available,
                Path = resolved,
                Name = Path.GetFileName(resolved),
                FileBytes = stream.Length,
                Version = EmptyToNull(FileVersionInfo.GetVersionInfo(resolved).FileVersion),
                Architecture = pe.PEHeaders.CoffHeader.Machine.ToString(),
                Sha256 = sha256,
                Signature = signature,
                SignerSubject = signer,
                Exports = exports,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(resolved, InventoryAccess.AccessDenied);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return Unavailable(resolved, InventoryAccess.ExclusiveAccessDenied);
        }
        catch (IOException)
        {
            return Unavailable(resolved, InventoryAccess.Disconnected);
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or CryptographicException or ArgumentException or InvalidOperationException
            or OverflowException or System.ComponentModel.Win32Exception)
        {
            return Unavailable(resolved, InventoryAccess.Malformed);
        }
    }

    private static NativeBinaryInventory Unavailable(
        string path,
        InventoryAccess access,
        long fileBytes = 0) => new()
        {
            Access = access,
            Path = path,
            Name = SafeFileName(path),
            FileBytes = fileBytes,
            Signature = BinarySignatureState.Unknown,
        };

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return "invalid-native-path";
        }
    }

    private static string Hash(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (BinarySignatureState State, string? Subject) ReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Authenticode PE signer extraction has no loader equivalent.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return (BinarySignatureState.Signed, EmptyToNull(certificate.Subject));
        }
        catch (CryptographicException)
        {
            return (BinarySignatureState.Unsigned, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (BinarySignatureState.Unknown, null);
        }
    }

    private static IReadOnlyList<string> ReadExports(PEReader pe)
    {
        DirectoryEntry directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0 || directory.Size < 40)
        {
            return [];
        }

        BlobReader table = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        if (table.RemainingBytes < 40)
        {
            return [];
        }

        table.Offset = 24;
        uint nameCount = table.ReadUInt32();
        _ = table.ReadUInt32();
        uint namePointerRva = table.ReadUInt32();
        _ = table.ReadUInt32();
        if (nameCount > InventoryLimits.MaximumNativeExports || namePointerRva == 0)
        {
            return [];
        }

        BlobReader names = pe.GetSectionData((int)namePointerRva).GetReader();
        if (names.RemainingBytes < checked((int)nameCount * sizeof(uint)))
        {
            return [];
        }

        List<string> exports = [];
        for (int index = 0; index < nameCount; index++)
        {
            uint nameRva = names.ReadUInt32();
            if (nameRva == 0)
            {
                continue;
            }

            BlobReader nameReader = pe.GetSectionData((int)nameRva).GetReader();
            List<byte> bytes = [];
            while (nameReader.RemainingBytes > 0 && bytes.Count < MaximumExportNameBytes)
            {
                byte next = nameReader.ReadByte();
                if (next == 0)
                {
                    break;
                }

                bytes.Add(next);
            }

            if (bytes.Count != 0)
            {
                exports.Add(System.Text.Encoding.ASCII.GetString([.. bytes]));
            }
        }

        exports.Sort(StringComparer.Ordinal);
        return exports;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
