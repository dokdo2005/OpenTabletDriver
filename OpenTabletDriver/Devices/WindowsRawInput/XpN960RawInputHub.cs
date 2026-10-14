using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Devices;

namespace OpenTabletDriver.Devices.WindowsRawInput
{
    /// <summary>
    /// Windows-only experimental Raw Input source for the XP-N960 MI_01/COL03 pen
    /// collection. It never opens the HID collection for reading and never changes
    /// its driver binding; it only consumes WM_INPUT reports supplied by Windows.
    /// </summary>
    [DeviceHub]
    [SupportedPlatform(PluginPlatform.Windows)]
    public sealed class XpN960RawInputHub : IDeviceHub
    {
        private const ushort VendorId = 0x5543;
        private const ushort ProductId = 0x3005;
        private const ushort DigitizerUsagePage = 0x000D;
        private const ushort PenUsage = 0x0002;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;
        private const uint WM_INPUT = 0x00FF;
        private const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000B;
        private const uint RIM_TYPEHID = 2;
        private const int HWND_MESSAGE = -3;

        private readonly object sync = new();
        private readonly Dictionary<nint, XpN960RawInputEndpoint> endpoints = [];
        private readonly Thread windowThread;
        private readonly ManualResetEventSlim ready = new();
        private Exception? startupException;
        private nint hwnd;
        private static XpN960RawInputHub? instance;
        private static readonly WindowProc WindowProcDelegate = StaticWindowProc;

        public XpN960RawInputHub()
        {
            windowThread = new Thread(RunMessageWindow)
            {
                Name = "OpenTabletDriver XP-N960 Raw Input",
                IsBackground = true
            };
            windowThread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out creating XP-N960 Raw Input message window.");
            if (startupException != null)
                throw new InvalidOperationException("Unable to initialize XP-N960 Raw Input backend.", startupException);
        }

        public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

        public IEnumerable<IDeviceEndpoint> GetDevices()
        {
            lock (sync)
                return endpoints.Values.Cast<IDeviceEndpoint>().ToArray();
        }

        private void RunMessageWindow()
        {
            try
            {
                instance = this;
                string className = $"OpenTabletDriver.XpN960RawInput.{Environment.ProcessId}";
                var windowClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpszClassName = className,
                    lpfnWndProc = WindowProcDelegate
                };
                if (RegisterClassEx(ref windowClass) == 0)
                    ThrowLastError(nameof(RegisterClassEx));

                hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
                    new IntPtr(HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                    ThrowLastError(nameof(CreateWindowEx));

                var registration = new RAWINPUTDEVICE
                {
                    usUsagePage = DigitizerUsagePage,
                    usUsage = PenUsage,
                    dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                    hwndTarget = hwnd
                };
                if (!RegisterRawInputDevices([registration], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                    ThrowLastError(nameof(RegisterRawInputDevices));

                RefreshEndpoints();
                ready.Set();

                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                startupException = ex;
                Log.Exception(ex);
                ready.Set();
            }
        }

        private static nint StaticWindowProc(nint window, uint message, nuint wParam, nint lParam)
        {
            var hub = instance;
            if (hub == null)
                return DefWindowProc(window, message, wParam, lParam);

            try
            {
                if (message == WM_INPUT_DEVICE_CHANGE)
                    hub.RefreshEndpoints();
                else if (message == WM_INPUT)
                    hub.ProcessRawInput(lParam);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            return DefWindowProc(window, message, wParam, lParam);
        }

        private void RefreshEndpoints()
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>()) == uint.MaxValue)
            {
                LogLastError(nameof(GetRawInputDeviceList));
                return;
            }

            var devices = new RAWINPUTDEVICELIST[count];
            uint returned = GetRawInputDeviceList(devices, ref count, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
            if (returned == uint.MaxValue)
            {
                LogLastError(nameof(GetRawInputDeviceList));
                return;
            }

            var discovered = new Dictionary<nint, string>();
            foreach (var rawDevice in devices.Take((int)returned))
            {
                if (rawDevice.dwType != RIM_TYPEHID)
                    continue;
                var path = GetRawDeviceName(rawDevice.hDevice);
                var identity = GetRawHidIdentity(rawDevice.hDevice);
                if (path != null && identity.HasValue && IsXpN960PenCollection(path, identity.Value))
                    discovered.TryAdd(rawDevice.hDevice, path);
            }

            List<IDeviceEndpoint>? before = null;
            List<IDeviceEndpoint>? after = null;
            lock (sync)
            {
                var removed = endpoints.Keys.Except(discovered.Keys).ToArray();
                var added = discovered.Where(pair => !endpoints.ContainsKey(pair.Key)).ToArray();
                if (removed.Length == 0 && added.Length == 0)
                    return;

                before = endpoints.Values.Cast<IDeviceEndpoint>().ToList();
                foreach (var handle in removed)
                {
                    endpoints[handle].Complete();
                    endpoints.Remove(handle);
                }
                foreach (var (handle, path) in added)
                    endpoints.Add(handle, new XpN960RawInputEndpoint(path));
                after = endpoints.Values.Cast<IDeviceEndpoint>().ToList();
            }

            DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(before, after));
        }

        private void ProcessRawInput(nint input)
        {
            uint byteCount = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(input, RID_INPUT, IntPtr.Zero, ref byteCount, headerSize) == uint.MaxValue)
            {
                LogLastError(nameof(GetRawInputData));
                return;
            }
            if (byteCount < headerSize + 8 || byteCount > 1024 * 1024)
            {
                Log.Write(nameof(XpN960RawInputHub), $"Discarded invalid Raw Input size {byteCount}.", LogLevel.Warning);
                return;
            }

            var buffer = Marshal.AllocHGlobal((int)byteCount);
            try
            {
                if (GetRawInputData(input, RID_INPUT, buffer, ref byteCount, headerSize) == uint.MaxValue)
                {
                    LogLastError(nameof(GetRawInputData));
                    return;
                }
                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.dwType != RIM_TYPEHID || header.dwSize < headerSize + 8 || header.dwSize > byteCount)
                    return;

                XpN960RawInputEndpoint? endpoint;
                lock (sync)
                    endpoints.TryGetValue(header.hDevice, out endpoint);
                if (endpoint == null)
                    return;

                var hidDataLength = checked((int)header.dwSize - (int)headerSize);
                var hidData = new byte[hidDataLength];
                Marshal.Copy(buffer + (int)headerSize, hidData, 0, hidDataLength);
                if (hidData.Length < 8)
                    return;

                uint reportLength = BitConverter.ToUInt32(hidData, 0);
                uint reportCount = BitConverter.ToUInt32(hidData, 4);
                if (reportLength != 8 || reportCount == 0 || 8UL + (ulong)reportLength * reportCount > (ulong)hidData.Length)
                {
                    Log.Write(nameof(XpN960RawInputHub), "Discarded malformed XP-N960 RAWHID batch.", LogLevel.Warning);
                    return;
                }

                for (uint i = 0; i < reportCount; i++)
                {
                    var report = new byte[reportLength];
                    Buffer.BlockCopy(hidData, checked(8 + (int)(i * reportLength)), report, 0, (int)reportLength);
                    if (report[0] == 0x0A)
                        endpoint.Publish(report);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string? GetRawDeviceName(nint device)
        {
            uint characters = 0;
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref characters) == uint.MaxValue)
                return null;
            var buffer = Marshal.AllocHGlobal(checked(((int)characters + 1) * sizeof(char)));
            try
            {
                if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref characters) == uint.MaxValue)
                    return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static RawHidIdentity? GetRawHidIdentity(nint device)
        {
            // RID_DEVICE_INFO begins with cbSize and dwType, followed by the HID
            // variant of its union. The fixed 32-byte layout is documented by
            // GetRawInputDeviceInfo(RIDI_DEVICEINFO) for RIM_TYPEHID devices.
            const int deviceInfoSize = 32;
            var buffer = Marshal.AllocHGlobal(deviceInfoSize);
            try
            {
                Marshal.WriteInt32(buffer, deviceInfoSize);
                uint size = deviceInfoSize;
                if (GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, buffer, ref size) == uint.MaxValue || size < 24)
                    return null;
                if ((uint)Marshal.ReadInt32(buffer, 4) != RIM_TYPEHID)
                    return null;

                return new RawHidIdentity(
                    (ushort)Marshal.ReadInt32(buffer, 8),
                    (ushort)Marshal.ReadInt32(buffer, 12),
                    (ushort)Marshal.ReadInt32(buffer, 20),
                    (ushort)Marshal.ReadInt32(buffer, 22));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool IsXpN960PenCollection(string path, RawHidIdentity identity) =>
            identity.VendorId == VendorId &&
            identity.ProductId == ProductId &&
            identity.UsagePage == DigitizerUsagePage &&
            identity.Usage == PenUsage &&
            path.Contains("VID_5543&PID_3005&MI_01&COL03", StringComparison.OrdinalIgnoreCase);

        private static void ThrowLastError(string api) => throw new Win32Exception(Marshal.GetLastWin32Error(), api);
        private static void LogLastError(string api) => Log.Write(nameof(XpN960RawInputHub), new Win32Exception(Marshal.GetLastWin32Error(), api).Message, LogLevel.Warning);

        private sealed class XpN960RawInputEndpoint : IDeviceEndpoint
        {
            private readonly BlockingCollection<byte[]> reports = new(4096);

            internal XpN960RawInputEndpoint(string devicePath) => DevicePath = devicePath;

            public int ProductID => ProductId;
            public int VendorID => VendorId;
            public int InputReportLength => 8;
            public int OutputReportLength => 0;
            public int FeatureReportLength => 0;
            public string Manufacturer => "UC-LOGIC";
            public string ProductName => "N960";
            public string FriendlyName => "N960 Raw Input (experimental)";
            public string SerialNumber => string.Empty;
            public string DevicePath { get; }
            public bool CanOpen => !reports.IsAddingCompleted;
            public IDictionary<string, string> DeviceAttributes { get; } = new Dictionary<string, string>
            {
                ["USB_INTERFACE_NUMBER"] = "1",
                ["HID_REPORTS"] = "0A:000D:0002",
                ["InputBackend"] = "WindowsRawInputExperimental"
            };

            public IDeviceEndpointStream Open() => new XpN960RawInputStream(reports);
            public string? GetDeviceString(byte index) => null;
            public void OpenAndSendFeatureInit(byte[] initData) => throw new NotSupportedException("Raw Input endpoint has no feature-report transport.");
            public void OpenAndSendOutputInit(byte[] initData) => throw new NotSupportedException("Raw Input endpoint has no output-report transport.");
            internal void Complete() => reports.CompleteAdding();
            internal void Publish(byte[] report)
            {
                if (!reports.IsAddingCompleted && !reports.TryAdd(report))
                    Log.Write(nameof(XpN960RawInputHub), "Dropped XP-N960 report because the Raw Input queue is full.", LogLevel.Warning);
            }
        }

        private sealed class XpN960RawInputStream(BlockingCollection<byte[]> reports) : IDeviceEndpointStream
        {
            public byte[] Read()
            {
                try
                {
                    return reports.Take();
                }
                catch (InvalidOperationException ex)
                {
                    throw new IOException("XP-N960 Raw Input device was removed.", ex);
                }
            }
            public void Write(byte[] buffer) => throw new NotSupportedException("Raw Input endpoint is input-only.");
            public void GetFeature(byte[] buffer) => throw new NotSupportedException("Raw Input endpoint has no feature-report transport.");
            public void SetFeature(byte[] buffer) => throw new NotSupportedException("Raw Input endpoint has no feature-report transport.");
            public void Dispose() { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public nint hwndTarget; }
        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public nint hDevice; public uint dwType; }
        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public nint hDevice, wParam; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEX { public uint cbSize, style; public WindowProc? lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public nint hIconSm; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        private readonly record struct RawHidIdentity(ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);
        private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? devices, ref uint count, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, nint window, uint minimum, uint maximum);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref MSG message);
        [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG message);
    }
}
