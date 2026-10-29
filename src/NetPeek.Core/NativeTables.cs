using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace NetPeek.Core
{
    internal sealed class NativeTables : ITableSource
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Tcp4Row { internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
        [StructLayout(LayoutKind.Sequential)] internal struct Udp4Row { internal uint LocalAddress, LocalPort, Pid; }
        [StructLayout(LayoutKind.Sequential)] internal struct Tcp6Row
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalAddress;
            internal uint LocalScope, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] RemoteAddress;
            internal uint RemoteScope, RemotePort, State, Pid;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Udp6Row
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalAddress;
            internal uint LocalScope, LocalPort, Pid;
        }
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
        private static uint ReadTable(bool tcp, bool ipv6, IntPtr table, ref int size)
        {
            return tcp ? GetExtendedTcpTable(table, ref size, false, ipv6 ? 23U : 2U, 5, 0) : GetExtendedUdpTable(table, ref size, false, ipv6 ? 23U : 2U, 1, 0);
        }
        public List<EndpointRecord> Read(bool tcp, bool ipv6)
        {
            int required = 0;
            uint result = ReadTable(tcp, ipv6, IntPtr.Zero, ref required);
            if (result != 0 && result != 122) throw new Win32Exception((int)result);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (required < 4 || required > 64 * 1024 * 1024) throw new InvalidOperationException("端点表大小超出安全范围");
                int allocated = required;
                IntPtr buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    result = ReadTable(tcp, ipv6, buffer, ref required);
                    if (result == 122) continue;
                    if (result != 0) throw new Win32Exception((int)result);
                    return Decode(buffer, allocated, tcp, ipv6);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("端点表持续变化，已达到重试次数；稍后自动重试");
        }
        internal static List<EndpointRecord> Decode(IntPtr buffer, int length, bool tcp, bool ipv6)
        {
            Type rowType = tcp ? (ipv6 ? typeof(Tcp6Row) : typeof(Tcp4Row)) : (ipv6 ? typeof(Udp6Row) : typeof(Udp4Row));
            int stride = Marshal.SizeOf(rowType);
            if (length < 4) throw new InvalidOperationException("端点表头不完整");
            uint count = unchecked((uint)Marshal.ReadInt32(buffer));
            if (count > (length - 4) / stride) throw new InvalidOperationException("端点表记录数量超出缓冲区");
            List<EndpointRecord> rows = new List<EndpointRecord>((int)count);
            for (int index = 0; index < count; index++)
            {
                IntPtr ptr = IntPtr.Add(buffer, 4 + index * stride);
                EndpointRecord endpoint = new EndpointRecord { Protocol = tcp ? "TCP" : "UDP", AddressFamily = ipv6 ? "IPv6" : "IPv4", State = "BOUND" };
                uint pid;
                if (tcp && !ipv6)
                {
                    Tcp4Row row = (Tcp4Row)Marshal.PtrToStructure(ptr, rowType);
                    endpoint.LocalAddress = new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString();
                    endpoint.LocalPort = DecodePort(row.LocalPort);
                    endpoint.State = TcpState(row.State);
                    if (row.State != 2) { endpoint.RemoteAddress = new IPAddress(BitConverter.GetBytes(row.RemoteAddress)).ToString(); endpoint.RemotePort = DecodePort(row.RemotePort); }
                    pid = row.Pid;
                }
                else if (tcp)
                {
                    Tcp6Row row = (Tcp6Row)Marshal.PtrToStructure(ptr, rowType);
                    // Preserve scope DWORD as .NET Framework's SystemTcpConnection does.
                    // Live scoped-adapter tests guard against swapping its byte order.
                    endpoint.LocalAddress = new IPAddress(row.LocalAddress, row.LocalScope).ToString();
                    endpoint.LocalPort = DecodePort(row.LocalPort);
                    endpoint.State = TcpState(row.State);
                    if (row.State != 2) { endpoint.RemoteAddress = new IPAddress(row.RemoteAddress, row.RemoteScope).ToString(); endpoint.RemotePort = DecodePort(row.RemotePort); }
                    pid = row.Pid;
                }
                else if (!ipv6)
                {
                    Udp4Row row = (Udp4Row)Marshal.PtrToStructure(ptr, rowType);
                    endpoint.LocalAddress = new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString();
                    endpoint.LocalPort = DecodePort(row.LocalPort);
                    pid = row.Pid;
                }
                else
                {
                    Udp6Row row = (Udp6Row)Marshal.PtrToStructure(ptr, rowType);
                    endpoint.LocalAddress = new IPAddress(row.LocalAddress, row.LocalScope).ToString();
                    endpoint.LocalPort = DecodePort(row.LocalPort);
                    pid = row.Pid;
                }
                endpoint.Process.ProcessId = unchecked((int)pid);
                rows.Add(endpoint);
            }
            return rows;
        }
        internal static int DecodePort(uint port) { return (int)(((port & 255U) << 8) | ((port >> 8) & 255U)); }
        private static string TcpState(uint state)
        {
            string[] states = { "UNKNOWN", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RECEIVED", "ESTABLISHED", "FIN_WAIT_1", "FIN_WAIT_2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };
            return state < states.Length ? states[state] : "UNKNOWN(" + state + ")";
        }
    }
}
