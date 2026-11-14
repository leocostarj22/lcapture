// Contrast-adaptive sharpening (CAS-style, cross-shaped kernel) for a WPF ShaderEffect.
// Compile with tools/compile-shader.ps1 -> Sharpen.ps (target ps_2_0).

sampler2D input : register(s0);
float strength : register(c0);   // 0..1
float2 texel : register(c1);     // 1 / rendered size in pixels

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    float4 n = tex2D(input, uv + float2(0, -texel.y));
    float4 s = tex2D(input, uv + float2(0, texel.y));
    float4 w = tex2D(input, uv + float2(-texel.x, 0));
    float4 e = tex2D(input, uv + float2(texel.x, 0));

    float3 mn = min(c.rgb, min(min(n.rgb, s.rgb), min(w.rgb, e.rgb)));
    float3 mx = max(c.rgb, max(max(n.rgb, s.rgb), max(w.rgb, e.rgb)));

    // Sharpen less where local contrast is already high (avoids halos/ringing).
    float3 amp = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 0.0001)));
    float peak = -1.0 / lerp(8.0, 5.0, saturate(strength));
    float3 wgt = amp * peak;

    float3 rgb = (c.rgb + wgt * (n.rgb + s.rgb + w.rgb + e.rgb)) / (1.0 + 4.0 * wgt);
    return float4(saturate(rgb), c.a);
}
