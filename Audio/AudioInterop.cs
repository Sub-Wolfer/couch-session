using System.Runtime.InteropServices;

namespace CouchMode.Audio;

// Core Audio COM surface. IMMDeviceEnumerator is documented; IPolicyConfig is not, but it has
// been stable since Vista and is the same mechanism SoundVolumeView and nircmd use. There is
// still no public API for setting the system default endpoint, so this is the available option.
internal static class AudioCom
{
    internal const int S_OK = 0;

    internal enum DataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    [Flags]
    internal enum DeviceState : uint
    {
        Active = 0x01, Disabled = 0x02, NotPresent = 0x04, Unplugged = 0x08,
        All = 0x0F,
    }

    internal const uint STGM_READ = 0;

    // PKEY_Device_FriendlyName
    internal static readonly PropertyKey PkeyDeviceFriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;

        public readonly string? AsString() =>
            VarType == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(Pointer) : null;
    }

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(DataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out DeviceState state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    // --- Undocumented: the only way to set the system default endpoint ---

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used, but every preceding slot must be declared so the
        // vtable offsets line up. Signatures for the unused ones are intentionally opaque.
        int GetMixFormat(IntPtr a, IntPtr b);
        int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        int ResetDeviceFormat(IntPtr a);
        int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        int SetProcessingPeriod(IntPtr a, IntPtr b);
        int GetShareMode(IntPtr a, IntPtr b);
        int SetShareMode(IntPtr a, IntPtr b);
        int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        int SetEndpointVisibility(IntPtr a, int b);
    }
}
