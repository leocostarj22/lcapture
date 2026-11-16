using System.Runtime.InteropServices;

namespace LCapture.Interop;

/// <summary>Asks Media Foundation whether the GPU offers a hardware H.264 encoder.</summary>
internal static class MediaFoundationInterop
{
    private static readonly Guid MftCategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    private const uint MftEnumFlagHardware = 0x04;
    private const uint MftEnumFlagSortAndFilter = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType,
                                        in MftRegisterTypeInfo outputType, out IntPtr activates, out uint count);

    private static bool? s_hasHardwareH264;

    /// <summary>True/false when known; null if the check itself failed (then assume it is fine).</summary>
    public static bool? HasHardwareH264Encoder()
    {
        if (s_hasHardwareH264 is bool known) return known;

        try
        {
            var output = new MftRegisterTypeInfo { MajorType = MediaTypeVideo, Subtype = VideoFormatH264 };
            int hr = MFTEnumEx(MftCategoryVideoEncoder, MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                               IntPtr.Zero, in output, out var activates, out uint count);
            if (hr < 0) return null;

            for (int i = 0; i < count; i++)
                Marshal.Release(Marshal.ReadIntPtr(activates, i * IntPtr.Size));
            if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates);

            s_hasHardwareH264 = count > 0;
            return s_hasHardwareH264;
        }
        catch
        {
            return null;
        }
    }
}
