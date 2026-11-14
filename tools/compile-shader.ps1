# Compiles an HLSL pixel shader to bytecode using the D3DCompiler shipped with Windows,
# so no Windows SDK / fxc.exe is required.
# Usage: powershell -File tools\compile-shader.ps1 [-Source <hlsl>] [-Output <ps>]
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\src\LCapture\Shaders\Sharpen.hlsl"),
    [string]$Output = (Join-Path $PSScriptRoot "..\src\LCapture\Shaders\Sharpen.ps"),
    [string]$Target = "ps_2_0"
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID3DBlob
{
    [PreserveSig] IntPtr GetBufferPointer();
    [PreserveSig] UIntPtr GetBufferSize();
}

public static class D3DShaderCompiler
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, UIntPtr size, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);

    public static byte[] Compile(string hlsl, string entry, string target)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(hlsl);
        ID3DBlob code, errors;
        int hr = D3DCompile(bytes, (UIntPtr)bytes.Length, "shader.hlsl", IntPtr.Zero, IntPtr.Zero,
                            entry, target, 1 << 15 /* OPTIMIZATION_LEVEL3 */, 0, out code, out errors);
        if (hr < 0)
        {
            string msg = errors == null ? "unknown error" : Marshal.PtrToStringAnsi(errors.GetBufferPointer());
            throw new Exception("HLSL compile failed: " + msg);
        }
        int len = (int)code.GetBufferSize();
        byte[] result = new byte[len];
        Marshal.Copy(code.GetBufferPointer(), result, 0, len);
        return result;
    }
}
'@

$hlsl = Get-Content -Raw -Path $Source
$bytes = [D3DShaderCompiler]::Compile($hlsl, "main", $Target)
[System.IO.File]::WriteAllBytes($Output, $bytes)
"Compiled $Source -> $Output ($($bytes.Length) bytes, $Target)"
