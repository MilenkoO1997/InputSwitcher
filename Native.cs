using System.ComponentModel;
using System.Runtime.InteropServices;

namespace InputSwitcher;

internal static class Native
{
    const uint QdcOnlyActivePaths = 0x00000002;
    const int DisplayConfigDeviceInfoGetSourceName = 1;
    const int DisplayConfigDeviceInfoGetTargetName = 2;

    public static List<Display> LoadDisplays()
    {
        var names = ReadFriendlyNames();
        var monitors = new List<IntPtr>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Rect __, IntPtr ___) =>
        {
            monitors.Add(hMonitor);
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new DdcException("Windows could not list monitors.");

        var displays = new List<Display>();
        var number = 1;
        foreach (var hMonitor in monitors)
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            var device = "";
            if (GetMonitorInfo(hMonitor, ref info))
                device = info.DeviceName ?? "";

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                continue;

            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
                continue;

            names.TryGetValue(device, out var friendly);
            for (var i = 0; i < physical.Length; i++)
            {
                var description = (physical[i].Description ?? "").Trim();
                var display = new Display
                {
                    Number = number,
                    Handle = physical[i].Handle,
                    Model = BestModel(friendly?.Model, description, MonitorDeviceString(device)),
                    Manufacturer = friendly?.Manufacturer ?? "",
                    DeviceName = device,
                    Connector = friendly?.Connector ?? "",
                };
                if (!Fill(display))
                {
                    DestroyPhysicalMonitor(physical[i].Handle);
                    continue;
                }
                display.Number = number++;
                displays.Add(display);
            }
        }

        return displays;
    }

    public static void Release(IEnumerable<Display> displays)
    {
        foreach (var display in displays)
        {
            if (display.Handle != IntPtr.Zero)
                DestroyPhysicalMonitor(display.Handle);
        }
    }

    public static int? SetInput(Display display, int code)
    {
        if (display.Handle == IntPtr.Zero)
            throw new DdcException("This monitor handle is no longer valid. Press Refresh.");

        var written = false;
        Win32Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(120 * attempt);
            if (SetVCPFeature(display.Handle, 0x60, (uint)code))
            {
                written = true;
                break;
            }
            last = new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!written)
            throw new DdcException(
                $"Could not switch to 0x{code:x2}. {last?.Message ?? "SetVCPFeature failed"} ({last?.NativeErrorCode ?? 0}).");

        Thread.Sleep(200);
        return GetVcp(display.Handle, 0x60, out var current) ? current : null;
    }

    public static int SelfTest()
    {
        var caps = "(prot(monitor)type(LCD)vcp(02 10 12 14(05 08 0B) 16 60(11 12 0F 10) AC))";
        var sources = InputCatalog.ParseCapabilitiesInputs(caps);
        Expect(sources.Select(s => s.HexCode).ToArray(), ["0x11", "0x12", "0x0f", "0x10"]);
        Expect(InputCatalog.Sort(sources).Select(s => s.Name).Take(2).ToArray(), ["HDMI-1", "HDMI-2"]);
        Expect(InputCatalog.ParseCapabilitiesInputs("vcp(10 12)").Count, 0);

        var nested = InputCatalog.TryParseCapabilitiesInputs(
            "vcp(14(05 08) 60(11 12) AC)", out var nestedSources);
        Expect(nested, InputCatalog.CapsParseResult.ValuesFound);
        Expect(nestedSources.Select(s => s.HexCode).ToArray(), ["0x11", "0x12"]);

        var bare = InputCatalog.TryParseCapabilitiesInputs("vcp(10 12 60 AC)", out var bareSources);
        Expect(bare, InputCatalog.CapsParseResult.FeaturePresentNoValues);
        Expect(bareSources.Count, 0);

        var messy = InputCatalog.ParseCapabilitiesInputs("vcp(60(0x11,12,0Fh,13))");
        Expect(messy.Select(s => s.HexCode).ToArray(), ["0x11", "0x12", "0x0f", "0x13"]);
        Expect(InputCatalog.Label(0x13), "HDMI-3");
        Expect(InputCatalog.Label(0x14), "HDMI-4");

        Expect(InputCatalog.Label(0x05), "Composite video 1");
        Expect(InputCatalog.Label(0x0C), "Component video 1");
        Expect(InputCatalog.Label(0x99), "Input 99");
        Expect(InputCatalog.LabelFor(0x04, "HDMI"), "HDMI");
        Expect(InputCatalog.LabelFor(0x11, "HDMI"), "HDMI-1");
        Expect(InputCatalog.LabelFor(0x0F, "DisplayPort"), "DisplayPort-1");
        return 0;
    }

    static void Expect<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    static void Expect(string[] actual, string[] expected)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    /// <summary>
    /// Fills input list and current source. Returns false when the monitor
    /// answers neither capabilities nor feature 60 (skip silent panels).
    /// </summary>
    static bool Fill(Display display)
    {
        var gotCurrent = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(80 * attempt);
            if (GetVcp(display.Handle, 0x60, out var current))
            {
                display.CurrentCode = current;
                gotCurrent = true;
                break;
            }
        }

        var capabilities = ReadCapabilities(display.Handle);
        var parse = InputCatalog.TryParseCapabilitiesInputs(capabilities, out var sources);
        var gotCaps = !string.IsNullOrEmpty(capabilities);

        if (!gotCurrent && !gotCaps)
            return false;

        display.HasDdc = true;
        if (parse != InputCatalog.CapsParseResult.ValuesFound || sources.Count == 0)
        {
            sources = new();
            display.InputError = parse switch
            {
                InputCatalog.CapsParseResult.FeaturePresentNoValues =>
                    "This monitor lists input switching but did not report which inputs it has.",
                InputCatalog.CapsParseResult.FeatureMissing when gotCaps =>
                    "This monitor did not list input sources in its DDC capabilities.",
                _ =>
                    "Could not read the input list from this monitor.",
            };
        }

        // Include the active source even when it is missing from capabilities.
        if (display.CurrentCode is int code && sources.All(source => source.Code != code))
            sources.Add(new InputSource(code, InputCatalog.LabelFor(code, display.Connector)));

        // Prefer Windows cable type for the active input when the monitor's VESA label is wrong.
        if (display.CurrentCode is int active)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i].Code != active)
                    continue;
                var name = InputCatalog.LabelFor(active, display.Connector);
                if (!string.Equals(sources[i].Name, name, StringComparison.Ordinal))
                    sources[i] = new InputSource(active, name);
                break;
            }
        }

        display.Inputs = InputCatalog.Sort(sources);
        return true;
    }

    static bool GetVcp(IntPtr handle, byte code, out int current)
    {
        current = 0;
        if (!GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var value, out _))
            return false;
        current = (int)value;
        return true;
    }

    static string ReadCapabilities(IntPtr handle)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(100 * attempt);
            if (!GetCapabilitiesStringLength(handle, out var length) || length < 2)
                continue;

            var buffer = new byte[length];
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
                continue;

            var end = Array.IndexOf(buffer, (byte)0);
            if (end < 0)
                end = buffer.Length;
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, end);
            if (text.Length > 0)
                return text;
        }
        return "";
    }

    static string BestModel(string? friendly, string description, string deviceString)
    {
        foreach (var candidate in new[] { friendly, description, deviceString })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (candidate.Contains("Generic PnP", StringComparison.OrdinalIgnoreCase))
                continue;
            return candidate.Trim();
        }
        return string.IsNullOrWhiteSpace(description) ? deviceString.Trim() : description.Trim();
    }

    static string MonitorDeviceString(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return "";
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 0)
            ? (device.DeviceString ?? "").Trim()
            : "";
    }

    sealed class FriendlyName
    {
        public string Model { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Connector { get; init; } = "";
    }

    static Dictionary<string, FriendlyName> ReadFriendlyNames()
    {
        var map = new Dictionary<string, FriendlyName>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
                return map;
            if (pathCount == 0)
                return map;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (var i = 0; i < pathCount; i++)
            {
                var source = new DisplayConfigSourceDeviceName
                {
                    header = new DisplayConfigDeviceInfoHeader
                    {
                        type = DisplayConfigDeviceInfoGetSourceName,
                        size = Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0)
                    continue;

                var target = new DisplayConfigTargetDeviceName
                {
                    header = new DisplayConfigDeviceInfoHeader
                    {
                        type = DisplayConfigDeviceInfoGetTargetName,
                        size = Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    }
                };
                var model = "";
                var manufacturer = "";
                var connector = ConnectorName(paths[i].targetInfo.outputTechnology);
                if (DisplayConfigGetDeviceInfo(ref target) == 0)
                {
                    model = (target.monitorFriendlyDeviceName ?? "").Trim();
                    manufacturer = DecodeManufacturer(target.edidManufactureId);
                    if (string.IsNullOrEmpty(connector))
                        connector = ConnectorName(target.outputTechnology);
                }

                var gdi = (source.viewGdiDeviceName ?? "").Trim();
                if (gdi.Length > 0)
                    map[gdi] = new FriendlyName { Model = model, Manufacturer = manufacturer, Connector = connector };
            }
        }
        catch
        {
            return map;
        }

        return map;
    }

    static string DecodeManufacturer(ushort id)
    {
        if (id == 0)
            return "";
        var c1 = (char)('@' + ((id >> 10) & 0x1F));
        var c2 = (char)('@' + ((id >> 5) & 0x1F));
        var c3 = (char)('@' + (id & 0x1F));
        var text = new string(new[] { c1, c2, c3 });
        return text.All(c => c is >= 'A' and <= 'Z') ? text : "";
    }

    static string ConnectorName(uint technology) => technology switch
    {
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort",
        12 => "External DisplayPort",
        15 => "USB-C",
        0x80000000 => "Internal",
        _ => "",
    };

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayConfigDeviceInfoHeader
    {
        public int type;
        public int size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        IntPtr topology);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor, byte code, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, byte[] buffer, uint length);
}
