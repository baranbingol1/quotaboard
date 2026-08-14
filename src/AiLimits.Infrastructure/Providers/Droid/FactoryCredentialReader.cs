// SPDX-License-Identifier: Apache-2.0
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Droid;

public sealed class FactoryCredentialReader
{
    private static class WindowsAesGcm
    {
        private struct AuthenticatedCipherModeInfo
        {
            public int Size;

            public int Version;

            public nint Nonce;

            public int NonceSize;

            public nint AuthData;

            public int AuthDataSize;

            public nint Tag;

            public int TagSize;

            public nint MacContext;

            public int MacContextSize;

            public int AadSize;

            public long DataSize;

            public int Flags;
        }

        private const string AesAlgorithm = "AES";

        private const string ChainingModeProperty = "ChainingMode";

        private const string ChainingModeGcm = "ChainingModeGCM";

        private const string ObjectLengthProperty = "ObjectLength";

        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] tag, byte[] ciphertext)
        {
            nint algorithm = IntPtr.Zero;
            nint key2 = IntPtr.Zero;
            GCHandle gCHandle = default(GCHandle);
            GCHandle gCHandle2 = default(GCHandle);
            try
            {
                ThrowIfFailed(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0u));
                byte[] bytes = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                ThrowIfFailed(BCryptSetProperty(algorithm, "ChainingMode", bytes, bytes.Length, 0u));
                byte[] array = new byte[4];
                ThrowIfFailed(BCryptGetProperty(algorithm, "ObjectLength", array, array.Length, out var _, 0u));
                byte[] array2 = new byte[BitConverter.ToInt32(array)];
                ThrowIfFailed(
                    BCryptGenerateSymmetricKey(algorithm, out key2, array2, array2.Length, key, key.Length, 0u)
                );
                gCHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                gCHandle2 = GCHandle.Alloc(tag, GCHandleType.Pinned);
                AuthenticatedCipherModeInfo paddingInfo = new AuthenticatedCipherModeInfo
                {
                    Size = Marshal.SizeOf<AuthenticatedCipherModeInfo>(),
                    Version = 1,
                    Nonce = gCHandle.AddrOfPinnedObject(),
                    NonceSize = nonce.Length,
                    Tag = gCHandle2.AddrOfPinnedObject(),
                    TagSize = tag.Length,
                };
                byte[] array3 = new byte[ciphertext.Length];
                ThrowIfFailed(
                    BCryptDecrypt(
                        key2,
                        ciphertext,
                        ciphertext.Length,
                        ref paddingInfo,
                        IntPtr.Zero,
                        0,
                        array3,
                        array3.Length,
                        out var resultSize2,
                        0u
                    )
                );
                return array3.AsSpan(0, resultSize2).ToArray();
            }
            finally
            {
                if (gCHandle2.IsAllocated)
                {
                    gCHandle2.Free();
                }
                if (gCHandle.IsAllocated)
                {
                    gCHandle.Free();
                }
                if (key2 != IntPtr.Zero)
                {
                    BCryptDestroyKey(key2);
                }
                if (algorithm != IntPtr.Zero)
                {
                    BCryptCloseAlgorithmProvider(algorithm, 0u);
                }
                CryptographicOperations.ZeroMemory(key);
            }
        }

        private static void ThrowIfFailed(int status)
        {
            if (status < 0)
            {
                throw new CryptographicException("Windows could not decrypt the Factory credential store.");
            }
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(
            out nint algorithm,
            string algorithmId,
            string? implementation,
            uint flags
        );

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptSetProperty(
            nint handle,
            string property,
            byte[] input,
            int inputSize,
            uint flags
        );

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptGetProperty(
            nint handle,
            string property,
            byte[] output,
            int outputSize,
            out int resultSize,
            uint flags
        );

        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(
            nint algorithm,
            out nint key,
            byte[] keyObject,
            int keyObjectSize,
            byte[] secret,
            int secretSize,
            uint flags
        );

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(
            nint key,
            byte[] input,
            int inputSize,
            ref AuthenticatedCipherModeInfo paddingInfo,
            nint initializationVector,
            int initializationVectorSize,
            byte[] output,
            int outputSize,
            out int resultSize,
            uint flags
        );

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(nint key);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(nint algorithm, uint flags);
    }

    private readonly string _factoryHome;

    public FactoryCredentialReader(string? factoryHome = null)
    {
        _factoryHome =
            factoryHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".factory");
    }

    public async Task<FactoryCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        string envelopePath = Path.Combine(_factoryHome, "auth.v2.file");
        string keyPath = Path.Combine(_factoryHome, "auth.v2.key");
        if (!File.Exists(envelopePath) || !File.Exists(keyPath) || !OperatingSystem.IsWindows())
        {
            return null;
        }
        byte[]? keyBytes = null;
        byte[]? cleartextBytes = null;
        try
        {
            string envelope = await File.ReadAllTextAsync(envelopePath, cancellationToken).ConfigureAwait(false);
            string keyText = await File.ReadAllTextAsync(keyPath, cancellationToken).ConfigureAwait(false);
            string[] parts = envelope.Trim().Split(':');
            if (parts.Length != 3)
            {
                return null;
            }
            byte[] key = Convert.FromBase64String(keyText.Trim());
            keyBytes = key;
            byte[] nonce = Convert.FromBase64String(parts[0]);
            byte[] tag = Convert.FromBase64String(parts[1]);
            byte[] ciphertext = Convert.FromBase64String(parts[2]);
            byte[] cleartext = WindowsAesGcm.Decrypt(key, nonce, tag, ciphertext);
            cleartextBytes = cleartext;
            using JsonDocument document = JsonDocument.Parse(cleartext);
            JsonElement root = document.RootElement;
            string accessToken = ReadString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }
            return new FactoryCredential(
                accessToken,
                ReadString(root, "refresh_token"),
                ReadString(root, "active_organization_id") ?? ReadString(root, "organization_id"),
                ReadDisplayEmail(accessToken)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex2)
            when ((
                    (
                        ex2 is IOException
                        || ex2 is UnauthorizedAccessException
                        || ex2 is FormatException
                        || ex2 is JsonException
                        || ex2 is CryptographicException
                    )
                        ? 1
                        : 0
                ) != 0
            )
        {
            return null;
        }
        finally
        {
            if (keyBytes != null)
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
            if (cleartextBytes != null)
            {
                CryptographicOperations.ZeroMemory(cleartextBytes);
            }
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        JsonElement value;
        return (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.String)
            ? value.GetString()
            : null;
    }

    // The encrypted Factory credential is already trusted as the local CLI's
    // login material. JWT claims are decoded only to improve the display name;
    // they are never used to authorize a request or make a security decision.
    private static string? ReadDisplayEmail(string accessToken)
    {
        try
        {
            string[] segments = accessToken.Split('.');
            if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[1]))
            {
                return null;
            }
            string payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            string? email = ReadString(document.RootElement, "email")?.Trim();
            return !string.IsNullOrWhiteSpace(email) && email.Length <= 320 && email.Contains('@') ? email : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

public sealed record FactoryCredential(
    string AccessToken,
    string? RefreshToken,
    string? OrganizationId,
    string? Email = null
);
