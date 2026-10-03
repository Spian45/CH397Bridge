// CH397 user-mode bridge for Windows on ARM64.
//
// WCH ships no ARM64 driver for the CH336/CH339/CH396/CH397 USB Ethernet chips.
// This program talks to the chip through Microsoft's inbox WinUSB driver and
// forwards Ethernet frames to/from a TAP-Windows6 virtual adapter, which then
// behaves like a normal wired network card.
//
// Protocol reference: WCH's GPL Linux driver, https://github.com/WCHSoftGroup/ch397_linux
//
// Normally installed and started by install.cmd. Manual use:
//   ch397bridge.exe [--adapter "Ethernet (CH397)"] [--tap {ADAPTER-GUID}]
// Log: %ProgramData%\CH397Bridge\bridge.log

using System;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Ch397Bridge
{
    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_SYSTEM = 0x4, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints;
            public byte bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType; // 2 = bulk, 3 = interrupt
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WINUSB_SETUP_PACKET
        {
            public byte RequestType, Request;
            public ushort Value, Index, Length;
        }

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle dev, out IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Free(IntPtr iface);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte alt, out USB_INTERFACE_DESCRIPTOR desc);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(IntPtr iface, byte alt, byte index, out WINUSB_PIPE_INFORMATION info);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ControlTransfer(IntPtr iface, WINUSB_SETUP_PACKET setup, byte[] buf, int len, out int transferred, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipe, byte[] buf, int len, out int transferred, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_WritePipe(IntPtr iface, byte pipe, byte[] buf, int len, out int transferred, IntPtr ov);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_AbortPipe(IntPtr iface, byte pipe);

        public static Exception LastError(string what)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), what + " failed");
        }
    }

    static class Log
    {
        static readonly object Sync = new object();
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CH397Bridge");
        public static readonly string FilePath = Path.Combine(Folder, "bridge.log");

        public static void StartNewIfLarge()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (new FileInfo(FilePath).Length > 1024 * 1024) File.Delete(FilePath);
            }
            catch { }
        }

        public static void Write(string format, params object[] args)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + string.Format(format, args);
            lock (Sync)
            {
                Console.WriteLine(line);
                try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            }
        }
    }

    // One reusable overlapped I/O slot (native buffer + OVERLAPPED + event).
    sealed class OverlappedIo : IDisposable
    {
        readonly SafeFileHandle handle;
        readonly IntPtr buffer, overlapped, evt;
        readonly int size;

        public OverlappedIo(SafeFileHandle handle, int size)
        {
            this.handle = handle;
            this.size = size;
            buffer = Marshal.AllocHGlobal(size);
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            evt = Native.CreateEvent(IntPtr.Zero, true, false, null);
        }

        void ResetOverlapped()
        {
            NativeOverlapped ov = new NativeOverlapped();
            ov.EventHandle = evt;
            Marshal.StructureToPtr(ov, overlapped, false);
        }

        int Finish(bool ok, string what)
        {
            if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING)
                throw Native.LastError(what);
            int n;
            if (!Native.GetOverlappedResult(handle, overlapped, out n, true))
                throw Native.LastError(what);
            return n;
        }

        public int Read(byte[] dest)
        {
            ResetOverlapped();
            int n = Finish(Native.ReadFile(handle, buffer, size, IntPtr.Zero, overlapped), "TAP read");
            Marshal.Copy(buffer, dest, 0, n);
            return n;
        }

        public void Write(byte[] src, int offset, int count)
        {
            Marshal.Copy(src, offset, buffer, count);
            ResetOverlapped();
            Finish(Native.WriteFile(handle, buffer, count, IntPtr.Zero, overlapped), "TAP write");
        }

        public void Dispose()
        {
            Native.CloseHandle(evt);
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(buffer);
        }
    }

    sealed class TapDevice : IDisposable
    {
        const uint TAP_IOCTL_GET_MAC = 0x220004;
        const uint TAP_IOCTL_SET_MEDIA_STATUS = 0x220018;

        readonly SafeFileHandle handle;
        readonly OverlappedIo reader, writer;
        public readonly byte[] Mac = new byte[6];
        public readonly string Name;

        public TapDevice(string guid)
        {
            Name = guid;
            handle = Native.CreateFile(@"\\.\Global\" + guid + ".tap", Native.GENERIC_READ | Native.GENERIC_WRITE, 0,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_SYSTEM | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
                throw Native.LastError("Opening TAP adapter " + guid);
            int n;
            if (!Native.DeviceIoControl(handle, TAP_IOCTL_GET_MAC, Mac, 6, Mac, 6, out n, IntPtr.Zero))
                throw Native.LastError("TAP get MAC");
            reader = new OverlappedIo(handle, 4096);
            writer = new OverlappedIo(handle, 4096);
        }

        // Finds a TAP-Windows6 adapter, preferring the one with the given connection name.
        public static string FindAdapterGuid(string preferredName)
        {
            string fallback = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!ni.Description.StartsWith("TAP-Windows Adapter V9", StringComparison.OrdinalIgnoreCase)) continue;
                if (preferredName != null && string.Equals(ni.Name, preferredName, StringComparison.OrdinalIgnoreCase)) return ni.Id;
                if (fallback == null) fallback = ni.Id;
            }
            return preferredName == null ? fallback : null;
        }

        public void SetMediaConnected(bool connected)
        {
            byte[] status = BitConverter.GetBytes(connected ? 1 : 0);
            int n;
            if (!Native.DeviceIoControl(handle, TAP_IOCTL_SET_MEDIA_STATUS, status, 4, status, 4, out n, IntPtr.Zero))
                throw Native.LastError("TAP set media status");
        }

        public int Read(byte[] dest) { return reader.Read(dest); }

        public void Write(byte[] src, int offset, int count) { writer.Write(src, offset, count); }

        public void CancelIo() { Native.CancelIoEx(handle, IntPtr.Zero); }

        public void Dispose()
        {
            try { SetMediaConnected(false); } catch { }
            CancelIo();
            handle.Dispose();
            reader.Dispose();
            writer.Dispose();
        }
    }

    sealed class Ch397 : IDisposable
    {
        // Vendor control requests
        const byte USB_GET_INFO = 0x10, USB_RD_REG = 0x11, USB_WR_REG = 0x12, USB_RD_PHY = 0x15, WR_ETH_MACCFG = 0x1E;

        // Registers
        const uint ETH_MAC_CFG = 0x40000700, ETH_MAC_H = 0x40000710, ETH_MAC_L = 0x40000714, ETH_BMSR = 0x00000740;
        const uint ETH_MAC_HTHR = 0x40000730, ETH_MAC_HTLR = 0x40000734;

        // MAC configuration bits
        const uint MEDIUM_PS = 1u << 7, MEDIUM_FD = 1u << 8, FLOWCTRL_EN = 1u << 11, TX_CTRL_EN = 1u << 13, RX_CTRL_EN = 1u << 14;
        const uint RX_CTRL_PBD = 1u << 22, RX_CTRL_PUF = 1u << 21, RX_CTRL_PAM = 1u << 20, RX_CTRL_RA = 1u << 19;

        public const int TxOverhead = 8, RxOverhead = 8;

        static readonly string[] SupportedIds = { "vid_1a86&pid_5397", "vid_1a86&pid_5396", "vid_1a86&pid_5395", "vid_1a86&pid_5394" };
        static Guid GUID_DEVINTERFACE_USB_DEVICE = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        readonly SafeFileHandle file;
        readonly IntPtr usb;
        readonly object controlLock = new object();
        public byte BulkIn, BulkOut, InterruptIn;
        public byte FirmwareVersion;
        public readonly byte[] Mac = new byte[6];

        public static string FindDevicePath()
        {
            IntPtr set = Native.SetupDiGetClassDevs(ref GUID_DEVINTERFACE_USB_DEVICE, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1))
                throw Native.LastError("SetupDiGetClassDevs");
            try
            {
                for (int i = 0; ; i++)
                {
                    Native.SP_DEVICE_INTERFACE_DATA data = new Native.SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVINTERFACE_USB_DEVICE, i, ref data))
                        return null;
                    int required;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        foreach (string id in SupportedIds)
                            if (path.ToLowerInvariant().Contains(id))
                                return path;
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        public Ch397(string path)
        {
            file = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file.IsInvalid)
                throw Native.LastError("Opening USB device");
            if (!Native.WinUsb_Initialize(file, out usb))
            {
                Exception e = Native.LastError("WinUsb_Initialize (is the WinUSB driver installed for the adapter?)");
                file.Dispose();
                throw e;
            }

            Native.USB_INTERFACE_DESCRIPTOR desc;
            if (!Native.WinUsb_QueryInterfaceSettings(usb, 0, out desc))
                throw Native.LastError("WinUsb_QueryInterfaceSettings");
            for (byte i = 0; i < desc.bNumEndpoints; i++)
            {
                Native.WINUSB_PIPE_INFORMATION pipe;
                if (!Native.WinUsb_QueryPipe(usb, 0, i, out pipe))
                    throw Native.LastError("WinUsb_QueryPipe");
                bool input = (pipe.PipeId & 0x80) != 0;
                if (pipe.PipeType == 2 && input && BulkIn == 0) BulkIn = pipe.PipeId;
                else if (pipe.PipeType == 2 && !input && BulkOut == 0) BulkOut = pipe.PipeId;
                else if (pipe.PipeType == 3 && input && InterruptIn == 0) InterruptIn = pipe.PipeId;
            }
            if (BulkIn == 0 || BulkOut == 0)
                throw new InvalidOperationException("Adapter has no bulk endpoints on interface 0");
        }

        int Control(bool input, byte request, uint reg, byte[] data, int length)
        {
            Native.WINUSB_SETUP_PACKET setup = new Native.WINUSB_SETUP_PACKET();
            setup.RequestType = (byte)(input ? 0xC0 : 0x40); // vendor, device
            setup.Request = request;
            setup.Value = (ushort)(reg & 0xFFFF);
            setup.Index = (ushort)(reg >> 16);
            setup.Length = (ushort)length;
            int n;
            lock (controlLock)
            {
                if (!Native.WinUsb_ControlTransfer(usb, setup, data, length, out n, IntPtr.Zero))
                    throw Native.LastError(string.Format("Control request 0x{0:X2} reg 0x{1:X8}", request, reg));
                Thread.Sleep(10); // the WCH driver waits 10 ms after every request
            }
            return n;
        }

        byte[] ReadBytes(byte request, uint reg, int length)
        {
            byte[] buf = new byte[length];
            Control(true, request, reg, buf, length);
            return buf;
        }

        uint ReadMacCfg() { return BitConverter.ToUInt32(ReadBytes(USB_RD_REG, ETH_MAC_CFG, 4), 0); }

        ushort ReadPhy(int loc) { return BitConverter.ToUInt16(ReadBytes(USB_RD_PHY, ETH_BMSR | ((uint)loc << 16), 2), 0); }

        void WriteReg(uint reg, byte[] data, int offset, int length)
        {
            byte[] buf = new byte[length];
            Buffer.BlockCopy(data, offset, buf, 0, length);
            Control(false, USB_WR_REG, reg, buf, length);
        }

        void WriteMacCfg(uint value)
        {
            if (FirmwareVersion >= 0x37)
                Control(false, WR_ETH_MACCFG, value, null, 0);
            else
                WriteReg(ETH_MAC_CFG, BitConverter.GetBytes(value), 0, 4);
        }

        public void Initialize(byte[] newMac)
        {
            byte[] info = ReadBytes(USB_GET_INFO, 0, 8);
            FirmwareVersion = info[2];
            Buffer.BlockCopy(ReadBytes(USB_RD_REG, ETH_MAC_L, 4), 0, Mac, 0, 4);
            Buffer.BlockCopy(ReadBytes(USB_RD_REG, ETH_MAC_H, 2), 0, Mac, 4, 2);
            Log.Write("CH397 chip type 0x{0:X2}, hw 0x{1:X2}, fw 0x{2:X2}, MAC {3}", info[0], info[1], info[2], FormatMac(Mac));

            // Use the TAP adapter's MAC so frames Windows sends/expects match the chip's unicast filter.
            WriteReg(ETH_MAC_L, newMac, 0, 4);
            WriteReg(ETH_MAC_H, newMac, 4, 2);
            Buffer.BlockCopy(newMac, 0, Mac, 0, 6);

            // Accept all multicast (IPv6 neighbour discovery, mDNS, etc.).
            byte[] allOnes = { 0xFF, 0xFF, 0xFF, 0xFF };
            WriteReg(ETH_MAC_HTHR, allOnes, 0, 4);
            WriteReg(ETH_MAC_HTLR, allOnes, 0, 4);
            ConfigureLink();
        }

        // Equivalent of ch397_link_reset + rx-mode setup in the Linux driver.
        public string ConfigureLink()
        {
            ushort bmcr = ReadPhy(0), advertise = ReadPhy(4), lpa = ReadPhy(5);
            bool speed100, fullDuplex;
            if ((bmcr & 0x1000) != 0) // autonegotiation enabled
            {
                int common = advertise & lpa;
                speed100 = (common & 0x0180) != 0;
                fullDuplex = (common & 0x0100) != 0 || (!speed100 && (common & 0x0040) != 0);
            }
            else
            {
                speed100 = (bmcr & 0x2000) != 0;
                fullDuplex = (bmcr & 0x0100) != 0;
            }

            uint cfg = ReadMacCfg();
            cfg = speed100 ? cfg | MEDIUM_PS : cfg & ~MEDIUM_PS;
            cfg = fullDuplex ? cfg | MEDIUM_FD : cfg & ~MEDIUM_FD;
            cfg |= FLOWCTRL_EN | RX_CTRL_EN | TX_CTRL_EN | RX_CTRL_PBD | RX_CTRL_PUF | RX_CTRL_PAM;
            cfg &= ~RX_CTRL_RA;
            WriteMacCfg(cfg);
            return (speed100 ? "100" : "10") + " Mbps " + (fullDuplex ? "full" : "half") + " duplex";
        }

        public bool ReadLinkFromPhy()
        {
            ReadPhy(1); // BMSR link bit is latched; the second read is current
            return (ReadPhy(1) & 0x0004) != 0;
        }

        public int ReadPipe(byte pipe, byte[] buf)
        {
            int n;
            if (!Native.WinUsb_ReadPipe(usb, pipe, buf, buf.Length, out n, IntPtr.Zero))
                throw Native.LastError("USB read");
            return n;
        }

        public void WritePipe(byte[] buf, int length)
        {
            int n;
            if (!Native.WinUsb_WritePipe(usb, BulkOut, buf, length, out n, IntPtr.Zero))
                throw Native.LastError("USB write");
        }

        public void Abort()
        {
            Native.WinUsb_AbortPipe(usb, BulkIn);
            Native.WinUsb_AbortPipe(usb, BulkOut);
            if (InterruptIn != 0) Native.WinUsb_AbortPipe(usb, InterruptIn);
        }

        public void Dispose()
        {
            Native.WinUsb_Free(usb);
            file.Dispose();
        }

        public static string FormatMac(byte[] mac)
        {
            return BitConverter.ToString(mac, 0, 6).Replace('-', ':');
        }
    }

    sealed class Bridge
    {
        // Received frames may be unpadded (e.g. 48-byte spanning-tree BPDUs), so accept anything with an Ethernet header.
        const int MinRxFrame = 14, MinFrame = 60, MaxFrame = 1514, MaxVlanFrame = 1518;

        readonly Ch397 nic;
        readonly TapDevice tap;
        readonly ManualResetEvent failed = new ManualResetEvent(false);
        volatile bool stopping;
        bool linkUp;

        // Receive stream reassembly: [u32 length][4 bytes][frame][pad to 4]...
        byte[] rxStream = new byte[65536];
        int rxLength;

        public Bridge(Ch397 nic, TapDevice tap)
        {
            this.nic = nic;
            this.tap = tap;
        }

        public WaitHandle Failed { get { return failed; } }

        public void Start()
        {
            SetLink(nic.ReadLinkFromPhy());
            StartThread("usb-rx", ReceiveLoop);
            StartThread("tap-rx", TransmitLoop);
            StartThread("link", LinkLoop);
        }

        public void Stop()
        {
            stopping = true;
            nic.Abort();
            tap.CancelIo();
        }

        void StartThread(string name, ThreadStart body)
        {
            Thread t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e)
                {
                    if (!stopping)
                    {
                        Log.Write("{0} stopped: {1}", name, e.Message);
                        failed.Set();
                    }
                }
            });
            t.Name = name;
            t.IsBackground = true;
            t.Start();
        }

        void SetLink(bool up)
        {
            if (up)
                Log.Write("Cable connected, {0}", nic.ConfigureLink());
            else
                Log.Write("Cable disconnected");
            linkUp = up;
            tap.SetMediaConnected(up);
        }

        void LinkLoop()
        {
            if (nic.InterruptIn == 0)
            {
                while (!stopping)
                {
                    Thread.Sleep(1000);
                    bool up = nic.ReadLinkFromPhy();
                    if (up != linkUp) SetLink(up);
                }
                return;
            }
            byte[] buf = new byte[64];
            while (!stopping)
            {
                int n = nic.ReadPipe(nic.InterruptIn, buf);
                if (n < 8) continue;
                bool up = (buf[0] & 0x40) != 0;
                if (up != linkUp) SetLink(up);
            }
        }

        void ReceiveLoop()
        {
            byte[] buf = new byte[16384];
            while (!stopping)
            {
                int n = nic.ReadPipe(nic.BulkIn, buf);
                if (n > 0) Feed(buf, n);
            }
        }

        void Feed(byte[] data, int count)
        {
            if (rxLength + count > rxStream.Length)
                rxLength = 0;
            Buffer.BlockCopy(data, 0, rxStream, rxLength, count);
            rxLength += count;

            int offset = 0;
            while (rxLength - offset >= Ch397.RxOverhead)
            {
                int frameLength = BitConverter.ToInt32(rxStream, offset);
                if (frameLength < MinRxFrame || frameLength > MaxVlanFrame)
                {
                    // Lost sync; drop everything and restart at the next USB transfer, like the Linux driver.
                    Log.Write("Bad receive header (length {0}), resynchronising", frameLength);
                    rxLength = 0;
                    return;
                }
                int padded = (frameLength + 3) & ~3;
                if (rxLength - offset < Ch397.RxOverhead + padded)
                    break;
                tap.Write(rxStream, offset + Ch397.RxOverhead, frameLength);
                offset += Ch397.RxOverhead + padded;
            }
            Buffer.BlockCopy(rxStream, offset, rxStream, 0, rxLength - offset);
            rxLength -= offset;
        }

        void TransmitLoop()
        {
            byte[] frame = new byte[4096];
            byte[] packet = new byte[4096];
            while (!stopping)
            {
                int n = tap.Read(frame);
                if (n < 14) continue;

                bool vlan = frame[12] == 0x81 && frame[13] == 0x00;
                int max = vlan ? MaxVlanFrame : MaxFrame;
                int dataLength = Math.Min(n, max);
                int packetLength = Math.Max(dataLength, MinFrame);
                int total = (packetLength + Ch397.TxOverhead + 3) & ~3;
                if (total == 512 || total == 1024)
                {
                    // Avoid transfers that are an exact multiple of the USB packet size (no zero-length packet needed).
                    total += 4;
                    packetLength += 4;
                }

                Array.Clear(packet, 0, total);
                BitConverter.GetBytes(packetLength).CopyTo(packet, 0);
                Buffer.BlockCopy(frame, 0, packet, Ch397.TxOverhead, dataLength);
                nic.WritePipe(packet, total);
            }
        }
    }

    static class Program
    {
        static volatile bool quit;
        static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);

        static int Main(string[] args)
        {
            string tapGuid = null, adapterName = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--tap" && i + 1 < args.Length) tapGuid = args[++i];
                else if (args[i] == "--adapter" && i + 1 < args.Length) adapterName = args[++i];
            }

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit = true; QuitEvent.Set(); };
            Log.StartNewIfLarge();
            Log.Write("CH397 bridge starting ({0}-bit process)", IntPtr.Size * 8);

            bool waitingLogged = false;
            while (!quit)
            {
                try
                {
                    if (tapGuid == null) tapGuid = TapDevice.FindAdapterGuid(adapterName);
                    if (tapGuid == null)
                    {
                        Log.Write("No TAP-Windows adapter{0} found. Run install.cmd first.",
                            adapterName == null ? "" : " named '" + adapterName + "'");
                        return 1;
                    }

                    string path = Ch397.FindDevicePath();
                    if (path == null)
                    {
                        if (!waitingLogged) Log.Write("Waiting for the CH397 adapter (WinUSB) to be plugged in...");
                        waitingLogged = true;
                        EjectDriverCd();
                        QuitEvent.WaitOne(2000);
                        continue;
                    }
                    waitingLogged = false;
                    RunSession(path, tapGuid);
                }
                catch (Exception e)
                {
                    Log.Write("Error: {0}", e.Message);
                }
                if (!quit) QuitEvent.WaitOne(3000);
            }
            Log.Write("Stopped");
            return 0;
        }

        // When plugged in, the adapter first shows up as a virtual CD holding WCH's Windows driver.
        // Ejecting that CD makes it re-enumerate as the network chip.
        static void EjectDriverCd()
        {
            const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                    if (!File.Exists(Path.Combine(drive.RootDirectory.FullName, @"WCHUSBNIC\WCHUSBNIC.INF"))) continue;
                    using (SafeFileHandle h = Native.CreateFile(@"\\.\" + drive.Name.TrimEnd('\\'), Native.GENERIC_READ,
                        Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        int n;
                        if (!h.IsInvalid && Native.DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, null, 0, null, 0, out n, IntPtr.Zero))
                            Log.Write("Ejected adapter driver CD {0} to switch it to network mode", drive.Name);
                    }
                }
                catch (IOException) { }
            }
        }

        static void RunSession(string path, string tapGuid)
        {
            using (TapDevice tap = new TapDevice(tapGuid))
            using (Ch397 nic = new Ch397(path))
            {
                Log.Write("Opened adapter {0}", path);
                Log.Write("Using TAP adapter {0}, MAC {1}", tapGuid, Ch397.FormatMac(tap.Mac));
                nic.Initialize(tap.Mac);

                Bridge bridge = new Bridge(nic, tap);
                bridge.Start();
                Log.Write("Bridge running");
                WaitHandle.WaitAny(new WaitHandle[] { bridge.Failed, QuitEvent });
                bridge.Stop();
                Thread.Sleep(200);
            }
        }
    }
}
