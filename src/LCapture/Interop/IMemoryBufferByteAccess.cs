using System.Runtime.InteropServices;

namespace LCapture.Interop;

/// <summary>
/// COM interface exposed by Windows.Foundation.IMemoryBufferReference on desktop .NET,
/// used to get a raw pointer into a SoftwareBitmap's pixel buffer. Not projected by
/// CsWinRT, so it must be declared manually (standard pattern from Microsoft samples).
/// </summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
