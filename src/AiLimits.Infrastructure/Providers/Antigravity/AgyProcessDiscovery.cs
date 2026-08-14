// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiLimits.Infrastructure.Providers.Antigravity;

internal sealed class AgyProcessDiscovery
{
    private const int AddressFamilyInterNetwork = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    public IReadOnlyList<int> FindListeningPorts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<int>();
        }

        HashSet<int> agyProcessIds = new();
        foreach (Process process in Process.GetProcessesByName("agy"))
        {
            using (process)
            {
                if (IsOwnedByCurrentUser(process))
                {
                    agyProcessIds.Add(process.Id);
                }
            }
        }
        if (agyProcessIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        return ReadTcpListeners()
            .Where(listener => agyProcessIds.Contains(listener.ProcessId))
            .Select(listener => listener.Port)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static bool IsOwnedByCurrentUser(Process process)
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            if (!OpenProcessToken(currentProcess.Handle, TokenQuery, out SafeAccessTokenHandle currentToken))
            {
                return false;
            }
            using (currentToken)
            {
                if (!OpenProcessToken(process.Handle, TokenQuery, out SafeAccessTokenHandle processToken))
                {
                    return false;
                }
                using (processToken)
                {
                    return TokensHaveSameUser(currentToken, processToken);
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TokensHaveSameUser(SafeAccessTokenHandle left, SafeAccessTokenHandle right)
    {
        IntPtr leftBuffer = ReadTokenUser(left);
        IntPtr rightBuffer = ReadTokenUser(right);
        if (leftBuffer == IntPtr.Zero || rightBuffer == IntPtr.Zero)
        {
            if (leftBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(leftBuffer);
            if (rightBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(rightBuffer);
            return false;
        }

        try
        {
            TokenUser leftUser = Marshal.PtrToStructure<TokenUser>(leftBuffer);
            TokenUser rightUser = Marshal.PtrToStructure<TokenUser>(rightBuffer);
            return EqualSid(leftUser.User.Sid, rightUser.User.Sid);
        }
        finally
        {
            Marshal.FreeHGlobal(leftBuffer);
            Marshal.FreeHGlobal(rightBuffer);
        }
    }

    private static IntPtr ReadTokenUser(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenInformationClass.User, IntPtr.Zero, 0, out int size);
        if (size <= 0)
        {
            return IntPtr.Zero;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        if (GetTokenInformation(token, TokenInformationClass.User, buffer, size, out _))
        {
            return buffer;
        }
        Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    private static IReadOnlyList<TcpListenerOwner> ReadTcpListeners()
    {
        int size = 0;
        uint result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            true,
            AddressFamilyInterNetwork,
            TcpTableClass.OwnerPidListener,
            0
        );
        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            return Array.Empty<TcpListenerOwner>();
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                true,
                AddressFamilyInterNetwork,
                TcpTableClass.OwnerPidListener,
                0
            );
            if (result != NoError)
            {
                return Array.Empty<TcpListenerOwner>();
            }

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPointer = IntPtr.Add(buffer, sizeof(int));
            List<TcpListenerOwner> listeners = new(count);
            for (int index = 0; index < count; index++)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                int port = (ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xffff));
                if (port > 0)
                {
                    listeners.Add(new TcpListenerOwner((int)row.OwningProcessId, port));
                }
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }
            return listeners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved
    );

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength
    );

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    private enum TcpTableClass
    {
        OwnerPidListener = 3,
    }

    private enum TokenInformationClass
    {
        User = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    private sealed record TcpListenerOwner(int ProcessId, int Port);
}
