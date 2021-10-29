using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DevProxy
{
    public class ProcessTcpConnection
    {
        public readonly IPEndPoint LocalEndpoint;
        public readonly IPEndPoint RemoteEndpoint;
        public readonly uint ProcessId;

        private ProcessTcpConnection(MIB_TCPROW_OWNER_PID row)
        {
            this.LocalEndpoint = new IPEndPoint(row.LocalAddress, row.LocalPort);
            this.RemoteEndpoint = new IPEndPoint(row.RemoteAddress, row.RemotePort);
            this.ProcessId = row.ProcessId;
        }

        public static List<ProcessTcpConnection> FindAllConnections(AddressFamily addressFamily)
        {
            switch(addressFamily)
            {
                case AddressFamily.InterNetwork:
                    return ProcessTcpConnection.GetAllTCPv4Connections().Select(c => new ProcessTcpConnection(c)).ToList();
                default:
                    throw new NotImplementedException();
            }
        }

        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int AF_INET = 2;    // IP_v4 = System.Net.Sockets.AddressFamily.InterNetwork
        private const int AF_INET6 = 23;  // IP_v6 = System.Net.Sockets.AddressFamily.InterNetworkV6


        private static List<MIB_TCPROW_OWNER_PID> GetAllTCPv4Connections()
        {
            return GetTCPConnections<MIB_TCPROW_OWNER_PID, MIB_TCPTABLE_OWNER_PID>(AF_INET);
        }

        // private static List<MIB_TCP6ROW_OWNER_PID> GetAllTCPv6Connections()
        // {
        //     return GetTCPConnections<MIB_TCP6ROW_OWNER_PID, MIB_TCP6TABLE_OWNER_PID>(AF_INET6);
        // }

        // https://www.pinvoke.net/default.aspx/iphlpapi/GetExtendedTcpTable.html
        private static List<IPR> GetTCPConnections<IPR, IPT>(int ipVersion)//IPR = Row Type, IPT = Table Type
        {
            IPR[] tableRows;

            var dwNumEntriesField = typeof(IPT).GetField("dwNumEntries");

            // how much memory do we need?
            IntPtr tcpTablePtr = IntPtr.Zero;
            int buffSize = 0;

            uint ret;
            try
            {
                while (ERROR_INSUFFICIENT_BUFFER == (ret = GetExtendedTcpTable(tcpTablePtr, ref buffSize, true, ipVersion, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL)))
                {
                    if (tcpTablePtr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tcpTablePtr);
                    }
                    tcpTablePtr = Marshal.AllocHGlobal(buffSize);
                }
            
                if (ret != 0)
                {
                    throw new Win32Exception((int)ret);
                }

                // get the number of entries in the table
                IPT table = (IPT)Marshal.PtrToStructure(tcpTablePtr, typeof(IPT));
                int rowStructSize = Marshal.SizeOf(typeof(IPR));
                uint numEntries = (uint)dwNumEntriesField.GetValue(table);

                // buffer we will be returning
                tableRows = new IPR[numEntries];

                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);
                for (int i = 0; i < numEntries; i++)
                {
                    IPR tcpRow = (IPR)Marshal.PtrToStructure(rowPtr, typeof(IPR));
                    tableRows[i] = tcpRow;
                    rowPtr = (IntPtr)((long)rowPtr + rowStructSize);   // next entry
                }
                return tableRows.ToList();
            }
            finally
            {
                // Free the Memory
                if (tcpTablePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(tcpTablePtr);
                }
            }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved = 0);

        private enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL,
            TCP_TABLE_OWNER_MODULE_LISTENER,
            TCP_TABLE_OWNER_MODULE_CONNECTIONS,
            TCP_TABLE_OWNER_MODULE_ALL
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint owningPid;

            public uint ProcessId
            {
                get { return owningPid; }
            }

            public IPAddress LocalAddress
            {
                get { return new IPAddress(localAddr); }
            }

            public ushort LocalPort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { localPort[1], localPort[0] }, 0);
                }
            }

            public IPAddress RemoteAddress
            {
                get { return new IPAddress(remoteAddr); }
            }

            public ushort RemotePort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { remotePort[1], remotePort[0] }, 0);
                }
            }

            public MIB_TCP_STATE State
            {
                get { return (MIB_TCP_STATE)state; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        private enum MIB_TCP_STATE
        {
            MIB_TCP_STATE_CLOSED = 1,
            MIB_TCP_STATE_LISTEN = 2,
            MIB_TCP_STATE_SYN_SENT = 3,
            MIB_TCP_STATE_SYN_RCVD = 4,
            MIB_TCP_STATE_ESTAB = 5,
            MIB_TCP_STATE_FIN_WAIT1 = 6,
            MIB_TCP_STATE_FIN_WAIT2 = 7,
            MIB_TCP_STATE_CLOSE_WAIT = 8,
            MIB_TCP_STATE_CLOSING = 9,
            MIB_TCP_STATE_LAST_ACK = 10,
            MIB_TCP_STATE_TIME_WAIT = 11,
            MIB_TCP_STATE_DELETE_TCB = 12
        }
    }
}
