using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LCapture.Effects;

/// <summary>GPU contrast-adaptive sharpening applied to the preview image (see Shaders/Sharpen.hlsl).</summary>
public sealed class SharpenEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/LCapture;component/Shaders/Sharpen.ps"),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(SharpenEffect), 0);

    public static readonly DependencyProperty StrengthProperty =
        DependencyProperty.Register(nameof(Strength), typeof(double), typeof(SharpenEffect),
            new UIPropertyMetadata(0.6, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty TexelSizeProperty =
        DependencyProperty.Register(nameof(TexelSize), typeof(Point), typeof(SharpenEffect),
            new UIPropertyMetadata(new Point(1.0 / 1920, 1.0 / 1080), PixelShaderConstantCallback(1)));

    public SharpenEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(StrengthProperty);
        UpdateShaderValue(TexelSizeProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>0 (subtle) to 1 (strong).</summary>
    public double Strength
    {
        get => (double)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    /// <summary>1 / rendered size in device pixels.</summary>
    public Point TexelSize
    {
        get => (Point)GetValue(TexelSizeProperty);
        set => SetValue(TexelSizeProperty, value);
    }
}
