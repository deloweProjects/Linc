using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Linc.Desktop.Services;

/// <summary>
/// One PC display, as the reverse mirror needs to describe it to the phone.
/// <paramref name="Left"/>/<paramref name="Top"/> are the display's origin within the virtual
/// desktop in physical pixels — needed to map a touch on this display's video into the
/// virtual-desktop coordinates <c>SendInput</c> uses (M05d). On the primary display they are 0.
/// </summary>
public sealed record PcDisplay(int Index, string Name, int Left, int Top, int Width, int Height, bool Primary);

/// <summary>
/// DXGI Desktop Duplication capture of a single PC display (M05, D-029). Produces BGRA
/// textures on the GPU which <see cref="PcVideoEncoder"/> encodes in place — on this path
/// there is no colour conversion at all, because the hardware H.264 MFT accepts ARGB32
/// (byte-identical to DXGI's B8G8R8A8) directly.
///
/// <para><b>Coordinate space.</b> Every size here is in <i>physical</i> pixels. That is only
/// true because the process is PerMonitorV2 DPI-aware (see app.manifest): a DPI-unaware
/// process is handed virtualised numbers by user32 <i>and by DXGI</i> — on this machine
/// 1536x864 instead of the real 1920x1080 — while gdi32's DESKTOPHORZRES keeps reporting
/// physical either way. With awareness set, capture space, virtual-desktop space and
/// SendInput's space all agree, which is what lets M05's input mapping stay simple.
/// The texture ring is nonetheless sized from the <i>acquired texture</i> rather than the
/// output description, so a mismatch can never silently turn CopyResource into a no-op.</para>
/// </summary>
public sealed class PcScreenCapture : IDisposable
{
    /// <summary>The D3D device the encoder must share, so frames never leave the GPU.</summary>
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>Physical pixel size of the captured display. Valid after the first frame.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    private readonly IDXGIOutput1 _output;
    /// <summary>Null between losing access (UAC, lock screen, a mode change) and getting it back.</summary>
    private IDXGIOutputDuplication? _duplication;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private int _next;

    /// <summary>
    /// The encoder keeps reading a submitted texture after ProcessInput returns, so we rotate
    /// rather than overwrite the one in flight — otherwise frames tear in a way that looks
    /// exactly like an encoder bug.
    /// </summary>
    private const int RingSize = 3;

    public PcScreenCapture(int displayIndex)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

        // The device lives on the graphics adapter that drives THIS display (D-087), numbered the
        // way Enumerate numbers them for the phone. The default adapter is not always it: on a
        // hybrid laptop or a desktop with two cards a display can hang off the other one, and
        // Desktop Duplication, the colour conversion and the encoder must all share one GPU.
        var (adapter, output) = FindDisplay(displayIndex)
            ?? throw new LincException($"This PC has no display {displayIndex} to mirror.");
        using (adapter)
        {
            // BgraSupport is required for the duplication surface format; VideoSupport for the
            // encoder. With an explicit adapter the driver type must be Unknown.
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels, out ID3D11Device? device).CheckError();
            Device = device ?? throw new LincException("This PC's graphics device couldn't be opened for mirroring.");
        }
        Context = Device.ImmediateContext;

        // The MFT encodes on its own thread while we capture on ours; both touch this device.
        using (var multithread = Device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        using (output)
            _output = output.QueryInterface<IDXGIOutput1>();
        _duplication = _output.DuplicateOutput(Device);
    }

    /// <summary>The adapter and output of attached display <paramref name="index"/>, in <see cref="Enumerate"/>'s order.</summary>
    private static (IDXGIAdapter1, IDXGIOutput)? FindDisplay(int index)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int seen = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
            {
                if (output.Description.AttachedToDesktop && seen++ == index) return (adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }

    /// <summary>Enumerate attached displays for the phone's display picker (<c>pc.displays</c>).</summary>
    public static IReadOnlyList<PcDisplay> Enumerate()
    {
        var displays = new List<PcDisplay>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop) continue;
                        var rect = description.DesktopCoordinates;
                        displays.Add(new PcDisplay(
                            displays.Count,
                            description.DeviceName,
                            rect.Left,
                            rect.Top,
                            rect.Right - rect.Left,
                            rect.Bottom - rect.Top,
                            rect.Left == 0 && rect.Top == 0));
                    }
                }
            }
        }
        return displays;
    }

    /// <summary>
    /// Grab the next frame into a texture we own. Returns false on timeout, which simply means
    /// the screen did not change — a normal, frequent outcome, not an error. Callers that must
    /// keep the stream flowing should resubmit <see cref="Last"/>.
    /// </summary>
    public bool TryAcquire(int timeoutMs, out ID3D11Texture2D? texture)
    {
        texture = null;
        if (_duplication is null && !Reacquire(timeoutMs)) return false;
        var result = _duplication!.AcquireNextFrame((uint)timeoutMs, out _, out IDXGIResource resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            // A UAC prompt, the lock screen, a full-screen game or a display mode change took the
            // desktop away. Not fatal: start over, and until then the feeder resends the last frame.
            _duplication.Dispose();
            _duplication = null;
            return false;
        }
        result.CheckError();

        try
        {
            using var frame = resource.QueryInterface<ID3D11Texture2D>();
            EnsureRing(frame.Description);
            if (frame.Description.Width != Width || frame.Description.Height != Height)
                throw new LincException("This display changed resolution. Start Mirror PC again on the phone.");
            texture = _ring[_next];
            _next = (_next + 1) % RingSize;
            Context.CopyResource(texture, frame);
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
        return true;
    }

    /// <summary>The most recently filled texture — resubmit this when the screen is static.</summary>
    public ID3D11Texture2D Last => _ring[(_next + RingSize - 1) % RingSize];

    /// <summary>True once a frame has arrived and <see cref="Width"/>/<see cref="Height"/> are real.</summary>
    public bool HasFrame => _ring[0] is not null;

    /// <summary>
    /// Size the ring from the first acquired frame. Taking the size from the output description
    /// instead would be a latent trap: if the two ever disagreed, CopyResource between mismatched
    /// textures fails silently (it returns void) and the mirror would stream stale or black frames
    /// with nothing in any log to say why.
    /// </summary>
    private void EnsureRing(Texture2DDescription frame)
    {
        if (_ring[0] is not null) return;

        Width = (int)frame.Width;
        Height = (int)frame.Height;
        for (int i = 0; i < RingSize; i++)
            _ring[i] = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = frame.Width,
                Height = frame.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = frame.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
    }

    /// <summary>
    /// Duplicate the display again after access was lost. On the secure desktop (UAC, lock screen)
    /// that keeps failing until it closes, so this waits out the caller's timeout and says no.
    /// </summary>
    private bool Reacquire(int timeoutMs)
    {
        try
        {
            _duplication = _output.DuplicateOutput(Device);
            return true;
        }
        catch
        {
            Thread.Sleep(Math.Max(timeoutMs, 1));
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var texture in _ring) texture?.Dispose();
        _duplication?.Dispose();
        _output.Dispose();
        Context?.Dispose();
        Device?.Dispose();
    }
}
