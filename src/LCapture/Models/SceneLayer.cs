using System.Text.Json.Serialization;

namespace LCapture.Models;

/// <summary>
/// A rectangle in normalised picture coordinates: 0..1 across the width and the height, so a
/// layout stays right at 720p, 1080p or 4K and in the recording.
/// </summary>
public readonly record struct RectN(double X, double Y, double W, double H)
{
    [JsonIgnore] public double Right => X + W;
    [JsonIgnore] public double Bottom => Y + H;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>This rectangle expressed relative to <paramref name="parent"/> (for attached layers).</summary>
    public RectN RelativeTo(RectN parent) => parent.W <= 0 || parent.H <= 0
        ? this
        : new((X - parent.X) / parent.W, (Y - parent.Y) / parent.H, W / parent.W, H / parent.H);

    /// <summary>A rectangle relative to <paramref name="parent"/> turned back into picture coordinates.</summary>
    public RectN Within(RectN parent) =>
        new(parent.X + X * parent.W, parent.Y + Y * parent.H, W * parent.W, H * parent.H);
}

public enum KeyColor { None, Black, White }

public enum SceneLayerKind
{
    /// <summary>A webcam (or any second video device).</summary>
    Camera,
    /// <summary>The captured window or screen.</summary>
    Window,
    /// <summary>The toolbar's video source, when a window is the main picture.</summary>
    Device,
    /// <summary>A picture file: logo, webcam frame, watermark. May be transparent.</summary>
    Image,
    /// <summary>A video file (WebM, MP4…) played in a loop, without sound: animated frames, logos.</summary>
    Media,
}

/// <summary>One layer drawn over the main picture. The list order is the drawing order (last on top).</summary>
public sealed class SceneLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SceneLayerKind Kind { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Camera: the device id. Image: the copied file in the app's data folder.</summary>
    public string? Source { get; set; }

    public RectN Rect { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Visible { get; set; } = true;

    /// <summary>The layer this one follows (moves and resizes with), e.g. a frame around a webcam.</summary>
    public Guid? AttachedTo { get; set; }

    /// <summary>Position relative to <see cref="AttachedTo"/>'s rectangle, while attached.</summary>
    public RectN RelativeRect { get; set; }

    /// <summary>Media layers: background colour made transparent (Windows' WebM decoders drop the alpha
    /// channel; transparent areas then come out white or black).</summary>
    public KeyColor Key { get; set; }

    /// <summary>Width/height of the content in pixels, once known (keeps the shape when resizing).</summary>
    public double Aspect { get; set; }

    /// <summary>Fed by a live source (frames keep arriving), as opposed to a still picture.</summary>
    [JsonIgnore] public bool IsVideo => Kind != SceneLayerKind.Image;

    /// <summary>Saved with the scene: the files and the webcams (windows change between sessions).</summary>
    [JsonIgnore] public bool IsSaved => Kind is SceneLayerKind.Camera or SceneLayerKind.Image or SceneLayerKind.Media;

    /// <summary>Key of the source feeding this layer, as used for frame routing.</summary>
    [JsonIgnore]
    public string SourceKey => Kind switch
    {
        SceneLayerKind.Camera => "camera:" + Source,
        SceneLayerKind.Window => "window",
        SceneLayerKind.Device => "device",
        SceneLayerKind.Media => "media:" + Id,
        _ => "image:" + Id,
    };
}
