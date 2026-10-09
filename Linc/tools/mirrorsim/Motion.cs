using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Linc.Desktop.Services;

/// <summary>
/// Puts real, hard-to-compress motion on the mirrored display for the length of a scenario.
///
/// Rate control can only be measured against content that asks for more bits than the target:
/// on an idle screen a correctly behaving VBR encoder sends a fraction of any target, so "the
/// output fell when the target fell" and "the controller found the link" are unfalsifiable.
/// Scrolling noise is the worst case for an encoder — every frame is new detail — so the stream
/// runs at whatever cap it is given, which is exactly what those scenarios need to observe.
///
/// A borderless topmost window on display 0 (the one the scenarios capture), open only while
/// the scenario runs. It changes nothing on the machine; closing it is the whole cleanup.
/// </summary>
sealed class Motion : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new(false);
    private NoiseForm? _form;

    /// <param name="block">Noise block size in pixels: smaller is harder to compress. 32 can be
    /// squeezed to ~1.5 Mb/s on the AMD encoder; 16 wants ~6 Mb/s at 8 Mb/s and can reach ~2.5.</param>
    public static Motion Start(int block = 32) => new(block);

    private Motion(int block)
    {
        var display = PcScreenCapture.Enumerate()[0];
        _thread = new Thread(() =>
        {
            _form = new NoiseForm(new Rectangle(display.Left, display.Top,
                Math.Min(1280, display.Width), Math.Min(720, display.Height)), block);
            _form.Shown += (_, _) => _shown.Set();
            Application.Run(_form);
        })
        { IsBackground = true, Name = "mirrorsim-motion" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_shown.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("the motion window never appeared");
        Thread.Sleep(300); // let the compositor put a few frames of it on screen
    }

    public void Dispose()
    {
        var form = _form;
        if (form is not null && form.IsHandleCreated)
        {
            try { form.BeginInvoke(form.Close); } catch (InvalidOperationException) { }
        }
        _thread.Join(3000);
        _shown.Dispose();
    }

    private sealed class NoiseForm : Form
    {
        private readonly Bitmap _noise;
        private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
        private int _offset;

        public NoiseForm(Rectangle bounds, int block)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;

            // Twice the window in each direction, so a scrolling window onto it is always full.
            // Stored at 1/Block resolution and drawn scaled up: coarse blocks of noise. Per-pixel
            // noise is incompressible — on the AMD encoder it measured ~7.9 Mb/s at EVERY target
            // from 1.5 to 6 Mb/s, while following 12 and 16 Mb/s exactly — which made rate
            // control look broken when it was the content that could not get smaller (D-069).
            // Blocks still want far more than any target at good quality, but can be squeezed.
            Block = block;
            _noise = new Bitmap(bounds.Width * 2 / Block, bounds.Height * 2 / Block, PixelFormat.Format32bppRgb);
            var data = _noise.LockBits(new Rectangle(Point.Empty, _noise.Size),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            var bytes = new byte[data.Stride * data.Height];
            new Random(1).NextBytes(bytes);
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            _noise.UnlockBits(data);

            _tick.Tick += (_, _) =>
            {
                _offset = (_offset + 17) % bounds.Width;
                Invalidate();
            };
            _tick.Start();
        }

        private readonly int Block;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.DrawImage(_noise, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                new Rectangle(_offset / Block, _offset / 2 / Block, ClientSize.Width / Block, ClientSize.Height / Block),
                GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tick.Dispose(); _noise.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
