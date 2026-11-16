using System.Runtime.InteropServices;

namespace LCapture.Interop;

/// <summary>
/// COM interface of Windows.Storage.Streams.Buffer that exposes its native memory, so frame
/// pixels can be copied straight into a media sample. Not projected by CsWinRT.
/// </summary>
[ComImport]
[Guid("905A0FEF-BC53-11DF-8C49-001E4FC686DA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IBufferByteAccess
{
    byte* Buffer();
}
