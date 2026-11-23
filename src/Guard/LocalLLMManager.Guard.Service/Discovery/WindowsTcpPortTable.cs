using System.Net;
using System.Runtime.InteropServices;

namespace LocalLLMManager.Guard.Service.Discovery;

public sealed record PortListenerInfo(
    int Port,
    string LocalAddress,
    int ProcessId,
    string AddressFamily);

/// <summary>
/// Inherited from LocalLLMManager.Infrastructure.Process.WindowsTcpPortTable.
/// Read-only Win32 IP Helper API wrapper (GetExtendedTcpTable) mapping TCP LISTEN ports to owning PIDs.
/// </summary>
public static class WindowsTcpPortTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public byte LocalPort1;
        public byte LocalPort2;
        public byte LocalPort3;
        public byte LocalPort4;
        public uint RemoteAddr;
        public byte RemotePort1;
        public byte RemotePort2;
        public byte RemotePort3;
        public byte RemotePort4;
        public uint OwningPid;

        public int LocalPort => (LocalPort1 << 8) | LocalPort2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public byte LocalPort1;
        public byte LocalPort2;
        public byte LocalPort3;
        public byte LocalPort4;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public byte RemotePort1;
        public byte RemotePort2;
        public byte RemotePort3;
        public byte RemotePort4;
        public uint State;
        public uint OwningPid;

        public int LocalPort => (LocalPort1 << 8) | LocalPort2;
    }

    public static IReadOnlyList<PortListenerInfo> GetAllListeningTcpPorts()
    {
        var results = new List<PortListenerInfo>();
        ReadIpv4Listeners(results);
        ReadIpv6Listeners(results);
        return results;
    }

    private static void ReadIpv4Listeners(List<PortListenerInfo> results)
    {
        int bufferSize = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AfInet, TcpTableOwnerPidListener);
        if (ret != ErrorInsufficientBuffer && ret != NoError)
            return;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref bufferSize, true, AfInet, TcpTableOwnerPidListener);
            if (ret != NoError)
                return;

            int rowCount = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, sizeof(int));
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                var addr = new IPAddress(row.LocalAddr).ToString();
                results.Add(new PortListenerInfo(row.LocalPort, addr, unchecked((int)row.OwningPid), "IPv4"));
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadIpv6Listeners(List<PortListenerInfo> results)
    {
        int bufferSize = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AfInet6, TcpTableOwnerPidListener);
        if (ret != ErrorInsufficientBuffer && ret != NoError)
            return;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref bufferSize, true, AfInet6, TcpTableOwnerPidListener);
            if (ret != NoError)
                return;

            int rowCount = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, sizeof(int));
            int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                var addr = row.LocalAddr is { Length: 16 }
                    ? new IPAddress(row.LocalAddr, row.LocalScopeId).ToString()
                    : "::";
                results.Add(new PortListenerInfo(row.LocalPort, addr, unchecked((int)row.OwningPid), "IPv6"));
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
