using System;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Linc.Desktop.Services;

/// <summary>
/// Converts captured BGRA frames to NV12 on the GPU, for encoders that won't take ARGB32 input
/// (D-077). Intel Quick Sync on a friend's PC shut itself down at START_OF_STREAM when fed ARGB32 (most likely the reason),
/// while AMD's encoder takes it directly; NV12 is the format every hardware H.264 encoder accepts.
/// The conversion is the D3D11 video processor — the same fixed-function block video players use —
/// so it costs no CPU and no copy back to system memory.
/// </summary>
public sealed class PcNv12Converter : IDisposable
{
    private const int RingSize = 3; // the encoder reads a submitted texture after Submit returns

    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D[] _outputs = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inputViews = new();
    private int _next;

    public PcNv12Converter(ID3D11Device device, int width, int height, int framesPerSecond)
    {
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)framesPerSecond, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational((uint)framesPerSecond, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoDevice.CreateVideoProcessorEnumerator(content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

        for (int i = 0; i < RingSize; i++)
        {
            _outputs[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _videoDevice.CreateVideoProcessorOutputView(_outputs[i], _enumerator,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D },
                out _outputViews[i]).CheckError();
        }
    }

    /// <summary>Convert one captured frame; returns an NV12 texture the encoder can take.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgra)
    {
        if (!_inputViews.TryGetValue(bgra.NativePointer, out var input))
        {
            _videoDevice.CreateVideoProcessorInputView(bgra, _enumerator,
                new VideoProcessorInputViewDescription
                {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                },
                out input).CheckError();
            _inputViews[bgra.NativePointer] = input;
        }
        int slot = _next;
        _next = (_next + 1) % RingSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        _videoContext.VideoProcessorBlt(_processor, _outputViews[slot], 0, 1, [stream]);
        return _outputs[slot];
    }

    public void Dispose()
    {
        foreach (var view in _inputViews.Values) view.Dispose();
        _inputViews.Clear();
        for (int i = 0; i < RingSize; i++) { _outputViews[i]?.Dispose(); _outputs[i]?.Dispose(); }
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
