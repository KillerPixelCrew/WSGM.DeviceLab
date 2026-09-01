using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Application;

/// <summary>One-use inherited authorization for a disposable Device Lab self-worker.</summary>
internal static class SelfWorkerAuthorization
{
    internal const int SecretBytes = 32;

    internal static byte[] CreateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    internal static string Hash(ReadOnlySpan<byte> secret) =>
        Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant();

    internal static async Task<byte[]?> ReadSecretAsync(
        string inheritedHandle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inheritedHandle))
        {
            return null;
        }

        byte[] secret = new byte[SecretBytes];
        try
        {
            using AnonymousPipeClientStream pipe = new(PipeDirection.In, inheritedHandle);
            int offset = 0;
            while (offset < secret.Length)
            {
                int read = await pipe.ReadAsync(secret.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return Reject(secret);
                }

                offset += read;
            }

            if (await pipe.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            {
                return Reject(secret);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return Reject(secret);
        }
        catch (OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }

        return secret;
    }

    private static byte[]? Reject(byte[] secret)
    {
        CryptographicOperations.ZeroMemory(secret);
        return null;
    }

    internal static bool VerifySecret(ReadOnlySpan<byte> secret, string? expectedSha256)
    {
        if (secret.Length != SecretBytes || expectedSha256 is not { Length: 64 })
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = SHA256.HashData(secret);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal static bool TryConstrainSessionFiles(
        string requestPath,
        string resultPath,
        string expectedRequestName,
        string expectedResultName,
        out string? constrainedRequest,
        out string? constrainedResult)
    {
        constrainedRequest = null;
        constrainedResult = null;
        try
        {
            string request = Path.GetFullPath(requestPath);
            string result = Path.GetFullPath(resultPath);
            string? requestDirectory = Path.GetDirectoryName(request);
            string? resultDirectory = Path.GetDirectoryName(result);
            if (requestDirectory is null
                || resultDirectory is null
                || !string.Equals(requestDirectory, resultDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(request), expectedRequestName, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(result), expectedResultName, StringComparison.Ordinal)
                || !Directory.Exists(requestDirectory)
                || ContainsLinkInAncestry(requestDirectory)
                || !File.Exists(request)
                || IsLink(request)
                || File.Exists(result)
                || Directory.Exists(result))
            {
                return false;
            }

            constrainedRequest = request;
            constrainedResult = result;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsLinkInAncestry(string directory)
    {
        string? current = directory;
        while (current is not null)
        {
            if (IsLink(current))
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
