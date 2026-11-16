using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using LCapture.Models;

namespace LCapture;

/// <summary>
/// Editing layers right on the picture: click a layer to select it, drag it to move it, pull a
/// corner to resize it (keeping its shape; Shift = free). Edges and the centre attract it. The
/// selection frame is a WPF overlay: it is never part of the recording.
/// </summary>
public partial class MainWindow
{
    private enum DragMode { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

    private const double HandleHitPixels = 9;
    private const double SnapDistance = 0.012;
    private const double MinLayerWidth = 0.03;

    private DragMode _dragMode;
    private SceneLayer? _dragLayer;
    private Point _dragStart;        // Normalised
    private RectN _dragStartRect;

    /// <summary>Where the picture is drawn inside the preview, in editor-canvas pixels.</summary>
    private Rect? PictureArea()
    {
        if (_previewBitmap is not { PixelWidth: > 0, PixelHeight: > 0 } bitmap) return null;
        if (PreviewImage.ActualWidth < 1 || PreviewImage.ActualHeight < 1) return null;

        var origin = PreviewImage.TranslatePoint(new Point(0, 0), EditorCanvas);
        double scale = Math.Min(PreviewImage.ActualWidth / bitmap.PixelWidth, PreviewImage.ActualHeight / bitmap.PixelHeight);
        double w = bitmap.PixelWidth * scale, h = bitmap.PixelHeight * scale;
        return new Rect(origin.X + (PreviewImage.ActualWidth - w) / 2, origin.Y + (PreviewImage.ActualHeight - h) / 2, w, h);
    }

    private Point ToNormalized(Point canvasPoint, Rect area) =>
        new((canvasPoint.X - area.X) / area.Width, (canvasPoint.Y - area.Y) / area.Height);

    private Rect ToCanvas(RectN rect, Rect area) =>
        new(area.X + rect.X * area.Width, area.Y + rect.Y * area.Height, rect.W * area.Width, rect.H * area.Height);

    /// <summary>Places the selection frame and handles over the selected layer (or hides them).</summary>
    private void UpdateSelectionAdorner()
    {
        var layer = _selectedLayer;
        bool show = layer is { Visible: true } && !LayersLocked && !ToolbarAutoHides && PictureArea() is not null;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SelectionBox.Visibility = visibility;
        foreach (var handle in new[] { HandleTL, HandleTR, HandleBL, HandleBR })
            handle.Visibility = visibility;
        if (!show) return;

        var box = ToCanvas(AbsoluteRect(layer!), PictureArea()!.Value);
        Canvas.SetLeft(SelectionBox, box.X - 1);
        Canvas.SetTop(SelectionBox, box.Y - 1);
        SelectionBox.Width = box.Width + 2;
        SelectionBox.Height = box.Height + 2;
        Place(HandleTL, box.Left, box.Top);
        Place(HandleTR, box.Right, box.Top);
        Place(HandleBL, box.Left, box.Bottom);
        Place(HandleBR, box.Right, box.Bottom);

        static void Place(Rectangle handle, double x, double y)
        {
            Canvas.SetLeft(handle, x - handle.Width / 2);
            Canvas.SetTop(handle, y - handle.Height / 2);
        }
    }

    private SceneLayer? HitLayer(Point normalized)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (layer.Visible && AbsoluteRect(layer).Contains(normalized.X, normalized.Y)) return layer;
        }
        return null;
    }

    private DragMode HitHandle(Point canvasPoint, Rect area)
    {
        if (_selectedLayer is not { Visible: true } layer) return DragMode.None;
        var box = ToCanvas(AbsoluteRect(layer), area);
        bool Near(double x, double y) =>
            Math.Abs(canvasPoint.X - x) <= HandleHitPixels && Math.Abs(canvasPoint.Y - y) <= HandleHitPixels;

        if (Near(box.Left, box.Top)) return DragMode.TopLeft;
        if (Near(box.Right, box.Top)) return DragMode.TopRight;
        if (Near(box.Left, box.Bottom)) return DragMode.BottomLeft;
        if (Near(box.Right, box.Bottom)) return DragMode.BottomRight;
        return DragMode.None;
    }

    private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!LayersLocked && !ToolbarAutoHides && PictureArea() is Rect area)
        {
            var point = e.GetPosition(EditorCanvas);
            var normalized = ToNormalized(point, area);
            var mode = HitHandle(point, area);
            var layer = mode != DragMode.None ? _selectedLayer : HitLayer(normalized);

            if (layer is not null)
            {
                if (layer != _selectedLayer) SelectLayer(layer);
                e.Handled = true; // A double click on a layer must not toggle fullscreen.
                if (e.ClickCount > 1) return;

                _dragMode = mode != DragMode.None ? mode : DragMode.Move;
                _dragLayer = layer;
                _dragStart = normalized;
                _dragStartRect = AbsoluteRect(layer);
                PreviewArea.CaptureMouse();
                return;
            }

            if (_selectedLayer is not null) SelectLayer(null);
        }

        if (e.ClickCount == 2) ToggleFullscreen();
    }

    private void PreviewArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (PictureArea() is not Rect area) return;
        var point = e.GetPosition(EditorCanvas);

        if (_dragMode == DragMode.None || _dragLayer is null)
        {
            // Hover feedback: what a click here would do.
            if (LayersLocked || ToolbarAutoHides)
            {
                PreviewArea.ClearValue(CursorProperty);
                return;
            }
            PreviewArea.Cursor = HitHandle(point, area) switch
            {
                DragMode.TopLeft or DragMode.BottomRight => Cursors.SizeNWSE,
                DragMode.TopRight or DragMode.BottomLeft => Cursors.SizeNESW,
                _ => HitLayer(ToNormalized(point, area)) is not null ? Cursors.SizeAll : null,
            };
            return;
        }

        var now = ToNormalized(point, area);
        var rect = _dragMode == DragMode.Move
            ? Snap(_dragStartRect with { X = _dragStartRect.X + now.X - _dragStart.X, Y = _dragStartRect.Y + now.Y - _dragStart.Y })
            : Resize(_dragStartRect, _dragMode, now, keepShape: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);

        SetLayerRect(_dragLayer, rect);
        PublishLayers(save: false);
    }

    private void PreviewArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        _dragLayer = null;
        PreviewArea.ReleaseMouseCapture();
        PublishLayers(); // Saves the new place.
    }

    /// <summary>Resizes from a corner, the opposite corner staying put.</summary>
    private RectN Resize(RectN start, DragMode corner, Point to, bool keepShape)
    {
        bool left = corner is DragMode.TopLeft or DragMode.BottomLeft;
        bool top = corner is DragMode.TopLeft or DragMode.TopRight;
        double anchorX = left ? start.Right : start.X;
        double anchorY = top ? start.Bottom : start.Y;

        double w = Math.Max(MinLayerWidth, left ? anchorX - to.X : to.X - anchorX);
        double h;
        if (keepShape)
        {
            double ratio = start.H / Math.Max(start.W, 1e-6);
            double fromHeight = (top ? anchorY - to.Y : to.Y - anchorY) / Math.Max(ratio, 1e-6);
            w = Math.Max(w, Math.Max(MinLayerWidth, fromHeight)); // Follow whichever way the mouse went further.
            h = w * ratio;
        }
        else
        {
            h = Math.Max(MinLayerWidth, top ? anchorY - to.Y : to.Y - anchorY);
        }

        return new RectN(left ? anchorX - w : anchorX, top ? anchorY - h : anchorY, w, h);
    }

    /// <summary>Pulls a moved layer onto the picture's edges, the usual margin, or the centre.</summary>
    private RectN Snap(RectN rect)
    {
        const double margin = 0.02;
        double aspect = CanvasAspect;
        double x = SnapAxis(rect.X, rect.W, margin, SnapDistance);
        double y = SnapAxis(rect.Y, rect.H, margin * aspect, SnapDistance * aspect);
        return rect with { X = x, Y = y };

        static double SnapAxis(double pos, double size, double margin, double distance)
        {
            double[] targets = [0, margin, (1 - size) / 2, 1 - margin - size, 1 - size];
            double best = pos, bestGap = distance;
            foreach (var target in targets)
            {
                double gap = Math.Abs(pos - target);
                if (gap < bestGap)
                {
                    best = target;
                    bestGap = gap;
                }
            }
            return best;
        }
    }

    // ---- Right-click menu ----

    private void PreviewArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (LayersLocked || ToolbarAutoHides || PictureArea() is not Rect area) return;
        if (HitLayer(ToNormalized(e.GetPosition(EditorCanvas), area)) is not { } layer) return;

        if (layer != _selectedLayer) SelectLayer(layer);
        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = PreviewArea, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuEntry("Ajustar ao ecrã", "\uE9A6", () => ApplyRect(layer, FitToScreen(layer))));
        menu.Items.Add(MenuEntry("Esticar para ocupar tudo", "\uE740", () => ApplyRect(layer, new RectN(0, 0, 1, 1))));

        var position = MenuEntry("Posição", "\uE8B0", null);
        foreach (var (label, where) in new[]
                 {
                     ("Canto superior esquerdo", "TopLeft"), ("Canto superior direito", "TopRight"),
                     ("Canto inferior esquerdo", "BottomLeft"), ("Canto inferior direito", "BottomRight"),
                     ("Ao centro", "Center"),
                 })
            position.Items.Add(MenuEntry(label, null, () => ApplyRect(layer, PlaceInCorner(AbsoluteRect(layer), where))));
        menu.Items.Add(position);

        menu.Items.Add(new Separator());
        int index = _layers.IndexOf(layer);
        var front = MenuEntry("Trazer para a frente", "\uE70E", () => MoveLayer(layer, +1));
        front.IsEnabled = index < _layers.Count - 1;
        var back = MenuEntry("Enviar para trás", "\uE70D", () => MoveLayer(layer, -1));
        back.IsEnabled = index > 0;
        menu.Items.Add(front);
        menu.Items.Add(back);
        menu.Items.Add(MenuEntry(layer.Kind == SceneLayerKind.Camera ? "Desligar a webcam" : "Esconder", "\uED1A",
                                 () => _ = ToggleLayerVisibleAsync(layer)));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Remover", "\uE74D", () => _ = RemoveLayerAsync(layer)));
        menu.IsOpen = true;
    }

    private MenuItem MenuEntry(string header, string? glyph, Action? onClick)
    {
        var item = new MenuItem { Header = header };
        if (glyph is not null)
            item.Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            };
        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    private void ApplyRect(SceneLayer layer, RectN rect)
    {
        SetLayerRect(layer, rect);
        PublishLayers();
    }

    /// <summary>The largest size that fits the picture without distorting the layer, centred.</summary>
    private RectN FitToScreen(SceneLayer layer)
    {
        var current = AbsoluteRect(layer);
        // The layer's shape on screen: from its content when known, otherwise as it is drawn now.
        double aspect = layer.Aspect > 0 ? layer.Aspect : current.W / Math.Max(current.H, 1e-6) * CanvasAspect;
        double w = 1, h = CanvasAspect / aspect;
        if (h > 1)
        {
            h = 1;
            w = aspect / CanvasAspect;
        }
        return new RectN((1 - w) / 2, (1 - h) / 2, w, h);
    }

    /// <summary>Keyboard: Delete removes the selected layer, Esc deselects it. True if handled.</summary>
    private bool HandleLayerKey(Key key)
    {
        if (_selectedLayer is null || Keyboard.FocusedElement is ComboBox or TextBox) return false;
        switch (key)
        {
            case Key.Delete:
                _ = RemoveLayerAsync(_selectedLayer);
                return true;
            case Key.Escape:
                SelectLayer(null);
                return true;
            default:
                return false;
        }
    }
}
