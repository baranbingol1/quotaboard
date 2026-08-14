// SPDX-License-Identifier: Apache-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AiLimits.Application.Abstractions;

namespace AiLimits.Platform.Windows.Security;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;

        public int Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;

        public int CredentialBlobSize;

        public nint CredentialBlob;

        public int Persist;

        public int AttributeCount;

        public nint Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    private const int CredTypeGeneric = 1;

    private const int CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    /// <summary>Credential Manager caps one blob at 2560 UTF-16 characters.</summary>
    private const int MaxSecretBytes = 5120;

    public Task SetAsync(string scope, string key, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string targetName = Target(scope, key);
        if (Encoding.Unicode.GetByteCount(secret) > MaxSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "Credential Manager secrets are limited to 2560 UTF-16 characters."
            );
        }
        byte[] bytes = Encoding.Unicode.GetBytes(secret);
        nint num = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, num, bytes.Length);
            NativeCredential userCredential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = num,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "QuotaBoard provider credential",
            };
            if (!CredWrite(ref userCredential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (num != IntPtr.Zero)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    Marshal.WriteByte(num, i, 0);
                }
                Marshal.FreeCoTaskMem(num);
            }
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(Target(scope, key), CredTypeGeneric, 0, out nint credentialPtr))
        {
            int lastWin32Error = Marshal.GetLastWin32Error();
            if (lastWin32Error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }
            throw new Win32Exception(lastWin32Error);
        }
        try
        {
            NativeCredential nativeCredential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            return Task.FromResult(
                (nativeCredential.CredentialBlobSize == 0)
                    ? string.Empty
                    : Marshal.PtrToStringUni(nativeCredential.CredentialBlob, nativeCredential.CredentialBlobSize / 2)
            );
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public Task DeleteAsync(string scope, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(Target(scope, key), CredTypeGeneric, 0))
        {
            int lastWin32Error = Marshal.GetLastWin32Error();
            if (lastWin32Error != ErrorNotFound)
            {
                throw new Win32Exception(lastWin32Error);
            }
        }
        return Task.CompletedTask;
    }

    private static string Target(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return "AiLimits/" + Uri.EscapeDataString(scope) + "/" + Uri.EscapeDataString(key);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, [In] int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out nint credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
