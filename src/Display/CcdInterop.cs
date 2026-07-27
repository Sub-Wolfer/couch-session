using System.Runtime.InteropServices;

namespace CouchMode.Display;

// P/Invoke surface for the Windows Connecting and Configuring Displays (CCD) API.
// This is what lets us enable/disable monitors and set the primary without DisplayFusion.
// Struct layouts are fixed by the SDK; sizes are asserted in DisplayManager's static ctor.
internal static class Ccd
{
    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        QueryFlags flags, out int numPathArrayElements, out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        QueryFlags flags,
        ref int numPathArrayElements, [Out] PathInfo[] pathArray,
        ref int numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int SetDisplayConfig(
        int numPathArrayElements, [In] PathInfo[]? pathArray,
        int numModeInfoArrayElements, [In] ModeInfo[]? modeInfoArray,
        SdcFlags flags);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName deviceName);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName deviceName);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetPreferredMode preferredMode);

    internal const int ERROR_SUCCESS = 0;

    [Flags]
    internal enum QueryFlags : uint
    {
        AllPaths = 0x01,
        OnlyActivePaths = 0x02,
        DatabaseCurrent = 0x04,
    }

    [Flags]
    internal enum SdcFlags : uint
    {
        UseSuppliedDisplayConfig = 0x00000020,
        Validate = 0x00000040,
        Apply = 0x00000080,
        NoOptimization = 0x00000100,
        SaveToDatabase = 0x00000200,
        AllowChanges = 0x00000400,
        AllowPathOrderChanges = 0x00002000,
    }

    internal enum DeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,

        /// <summary>The monitor's own preferred (native) mode, straight from its EDID.</summary>
        GetTargetPreferredMode = 3,
    }

    internal enum ModeInfoType : uint
    {
        Source = 1,
        Target = 2,
        DesktopImage = 3,
    }

    internal const uint PathActive = 0x00000001;
    internal const uint InvalidModeIndex = 0xffffffff;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
        public readonly bool Equals(Luid o) => LowPart == o.LowPart && HighPart == o.HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Region2D { public uint Cx; public uint Cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectL { public int Left, Top, Right, Bottom; }

    // ---- PATH (72 bytes) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    // ---- MODE (64 bytes: 16-byte header + 48-byte union) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public PointL Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq;
        public Rational VSyncFreq;
        public Region2D ActiveSize;
        public Region2D TotalSize;
        public uint VideoStandardAndFlags;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetMode { public VideoSignalInfo TargetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DesktopImageInfo
    {
        public PointL PathSourceSize;
        public RectL DesktopImageRegion;
        public RectL DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct ModeInfo
    {
        [FieldOffset(0)] public ModeInfoType InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public TargetMode TargetMode;
        [FieldOffset(16)] public SourceMode SourceMode;
        [FieldOffset(16)] public DesktopImageInfo DesktopImageInfo;
    }

    // ---- DEVICE INFO ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public DeviceInfoType Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    /// <summary>
    /// What the monitor says it would rather be run at.
    ///
    /// Width and height come from the panel's EDID, which is the only trustworthy answer to
    /// "what is this display's native resolution". Guessing it from the enumerated mode list
    /// cannot be made to work: a 4K television lists 4096×2160 alongside 3840×2160, so picking
    /// the largest is wrong, and an ultrawide lists 2560×1440 alongside 3440×1440, so picking
    /// the smallest at the tallest height is wrong too.
    ///
    /// Layout note: TargetMode begins with a ulong and so is 8-byte aligned, which puts four
    /// bytes of padding after Height. Marshal.SizeOf works this out the same way the C compiler
    /// did, which is why the header Size is taken from it rather than written as a literal.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetPreferredMode
    {
        public DeviceInfoHeader Header;
        public uint Width;
        public uint Height;
        public TargetMode TargetMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }
}
