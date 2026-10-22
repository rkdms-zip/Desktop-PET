using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPet;

public sealed class PetDrawing : FrameworkElement
{
    readonly BitmapSource[] typing;
    readonly Stopwatch clock = new();
    string state = "idle";
    int frame;
    public string State
    {
        get => state;
        set
        {
            if (state == value) return;
            state = value;
            frame = 1; // Both paws down at rest.
            if (state == "running") clock.Restart(); else clock.Reset();
            InvalidateVisual();
        }
    }
    public PetDrawing()
    {
        var sheet = new BitmapImage(new Uri("pack://application:,,,/Assets/cat-typing.png"));
        sheet.Freeze();
        typing = Enumerable.Range(0, 4).Select(i =>
        {
            var image = new CroppedBitmap(sheet, new Int32Rect(i % 2 * (sheet.PixelWidth / 2),
                i / 2 * (sheet.PixelHeight / 2), sheet.PixelWidth / 2, sheet.PixelHeight / 2));
            image.Freeze();
            return (BitmapSource)image;
        }).ToArray();
        frame = 1;
    }
    public static int FrameAt(string state, long milliseconds) => state == "running" ? (int)(Math.Max(0, milliseconds) / 150 % 4) : 1;
    public void Advance()
    {
        int next = FrameAt(state, clock.ElapsedMilliseconds);
        if (next == frame) return;
        frame = next;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        var image = typing[frame];
        double availableHeight = Math.Max(0, ActualHeight - 28);
        double scale = Math.Min(ActualWidth / image.PixelWidth, availableHeight / image.PixelHeight);
        double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
        var bounds = new Rect((ActualWidth - width) / 2, availableHeight - height, width, height);
        // Keep furniture, face, tail and hind paws fixed; animate only the front-paw region.
        d.DrawImage(typing[1], bounds);
        if (frame is 0 or 2)
        {
            d.PushClip(new RectangleGeometry(new Rect(bounds.X + width * .31, bounds.Y + height * .435,
                width * .385, height * .14), 3, 3));
            var aligned = bounds;
            aligned.Offset(width * .003, frame == 2 ? height * .04 : 0);
            d.DrawImage(image, aligned);
            d.Pop();
        }
    }
    public static int ExportPreview()
    {
        var pet = new PetDrawing { Width = 270, Height = 255 };
        pet.Measure(new Size(270, 255)); pet.Arrange(new Rect(0, 0, 270, 255));
        var encoder = new GifBitmapEncoder();
        for (int i = 0; i < 4; i++)
        {
            pet.frame = i;
            pet.InvalidateVisual(); pet.UpdateLayout();
            var rendered = new RenderTargetBitmap(270, 255, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(pet);
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)15);
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            if (i == 0)
            {
                metadata.SetQuery("/appext/application", System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                metadata.SetQuery("/appext/data", new byte[] { 3, 1, 0, 0, 0 });
            }
            encoder.Frames.Add(BitmapFrame.Create(rendered, null, metadata, null));
        }
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "typing-preview.gif"));
        encoder.Save(stream);
        return 0;
    }
}
