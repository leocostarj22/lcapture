using System.Runtime.InteropServices;

namespace LCapture.Interop;

/// <summary>
/// WASAPI "process loopback" (Windows 10 build 20348+ / Windows 11): captures the sound of one
/// process tree, or of everything except one process tree. NAudio 2.2 has no public API for it.
/// </summary>
internal static class ProcessLoopbackInterop
{
    public const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const uint AudclntStreamflagsLoopback = 0x00020000;
    public const uint AudclntStreamflagsEventCallback = 0x00040000;
    public const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    public const uint AudclntStreamflagsAutoConvertPcm = 0x80000000;
    public const uint AudclntBufferflagsSilent = 0x2;

    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientActivationParams
    {
        public int ActivationType; // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT holding a VT_BLOB (x64 layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobPropVariant
    {
        public ushort Vt; // VT_BLOB = 65
        public ushort Reserved1, Reserved2, Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                                     [In] ref WaveFormatEx format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
                                     [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags,
                                    out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }

    /// <summary>Marker interface (no methods): tells COM this object is free-threaded, so it is
    /// never wrapped in an apartment-marshaling proxy. Without it, the activated IAudioClient can
    /// come back through a proxy that fails to expose IAudioClient on cast (E_NOINTERFACE),
    /// even though the real object supports it.</summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject
    {
    }

    /// <summary>Completion callback; marked agile (free-threaded) so WASAPI's MTA call-back works.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _done.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object client);
                if (hr < 0) _done.TrySetException(Marshal.GetExceptionForHR(hr)!);
                else _done.TrySetResult((IAudioClient)client);
            }
            catch (Exception ex)
            {
                _done.TrySetException(ex);
            }
        }
    }
}
