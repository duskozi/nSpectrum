using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace nSpectrum.Wpf.Rendering;

/// <summary>Mirror of cbuffer Frame in Spectrum.hlsl (336 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Vector2 ViewSize;
    public float Time;
    public uint Columns;
    public float Rows;
    public float CellGap;
    public float BarGap;
    public float PeakThickness;
    public float DbGridStep;
    public float GridOffset;
    public float SplitY;
    public float WaterfallHead;
    public float ZoneYellow;
    public float ZoneRed;
    public float Glow;
    public float FillAlpha;
    public Vector4 ColorLow;
    public Vector4 ColorMid;
    public Vector4 ColorHigh;
    public Vector4 PeakColor;
    public Vector4 CurveColor;
    public Vector4 Background0;
    public Vector4 Background1;
    public Vector4 GridColor;
    public Vector4 UnlitColor;
    public FreqGridArray FreqGrid;
}

/// <summary>Mirror of cbuffer Post in Spectrum.hlsl (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PostConstants
{
    public Vector2 TargetSize;
    public Vector2 SourceTexel;
    public float BloomIntensity;
    public float Threshold;
    public float Vignette;
    public float BlurSpread;
}

[InlineArray(32)]
internal struct FreqGridArray
{
    private float _element;
}

/// <summary>
/// Direct3D 11 renderer: draws a skin into an HDR scene target, extracts and blurs the bright
/// parts at quarter resolution (bloom), and composites with vignette, soft clipping and dither
/// into a flip-model swap chain paced by a frame-latency waitable object.
/// Must be created, used and disposed on one thread (the render thread).
/// </summary>
internal sealed class D3DSpectrumRenderer : IDisposable
{
    public const int WaterfallRows = 512;

    private const Format BackBufferFormat = Format.B8G8R8A8_UNorm;
    private const Format HdrFormat = Format.R16G16B16A16_Float;
    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly nint _waitHandle;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader[] _skins = new ID3D11PixelShader[4];
    private readonly ID3D11PixelShader _bright, _blurH, _blurV, _composite;
    private readonly ID3D11Buffer _frameCb, _postCb;
    private readonly ID3D11SamplerState _wrap, _clamp;
    private readonly ID3D11ShaderResourceView?[] _nullViews = new ID3D11ShaderResourceView?[4];

    private ID3D11RenderTargetView? _backRtv;
    private RenderTarget? _scene, _bloomA, _bloomB;
    private ID3D11Buffer? _levels;
    private ID3D11ShaderResourceView? _levelsView;
    private int _levelsCapacity;
    private ID3D11Texture2D? _waterfall;
    private ID3D11ShaderResourceView? _waterfallView;
    private int _waterfallColumns;
    private int _waterfallHead;

    private sealed class RenderTarget(ID3D11Texture2D texture, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv, int width, int height) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public ID3D11RenderTargetView Rtv { get; } = rtv;
        public ID3D11ShaderResourceView Srv { get; } = srv;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Texture.Dispose(); }
    }

    public D3DSpectrumRenderer(nint hwnd, int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? device, out FeatureLevel featureLevel, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        FeatureLevel = featureLevel;

        using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
        using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
        {
            AdapterName = adapter.Description.Description;
            var desc = new SwapChainDescription1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = BackBufferFormat,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = Flags,
            };
            _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        }

        using (IDXGISwapChain2 sc2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            sc2.MaximumFrameLatency = 1;
            _waitHandle = sc2.FrameLatencyWaitableObject;
        }

        string source = LoadShaderSource();
        string sm = featureLevel >= FeatureLevel.Level_11_0 ? "5_0" : "4_0";
        _vs = _device.CreateVertexShader(Compiler.Compile(source, "VS", "Spectrum.hlsl", "vs_" + sm).Span);
        ID3D11PixelShader Ps(string entry) => _device.CreatePixelShader(Compiler.Compile(source, entry, "Spectrum.hlsl", "ps_" + sm).Span);
        _skins[0] = Ps("PS_Aurora");
        _skins[1] = Ps("PS_Rack");
        _skins[2] = Ps("PS_Matrix");
        _skins[3] = Ps("PS_Spectrogram");
        _bright = Ps("PS_Bright");
        _blurH = Ps("PS_BlurH");
        _blurV = Ps("PS_BlurV");
        _composite = Ps("PS_Composite");

        _frameCb = _device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _postCb = _device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<PostConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _wrap = _device.CreateSamplerState(SamplerDescription.LinearWrap);
        _clamp = _device.CreateSamplerState(SamplerDescription.LinearClamp);

        CreateTargets();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public FeatureLevel FeatureLevel { get; }
    public string AdapterName { get; } = "";

    /// <summary>Blocks until the swap chain can take a new frame (vsync-paced).</summary>
    public bool WaitForFrame(uint timeoutMs) =>
        _waitHandle == 0 || NativeMethods.WaitForSingleObjectEx(_waitHandle, timeoutMs, true) == NativeMethods.WAIT_OBJECT_0;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        _context.ClearState();
        ReleaseTargets();
        _context.Flush();
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, BackBufferFormat, Flags).CheckError();
        CreateTargets();
    }

    private void CreateTargets()
    {
        using (ID3D11Texture2D back = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _backRtv = _device.CreateRenderTargetView(back);
        _scene = CreateTarget(Width, Height);
        int qw = Math.Max(1, Width / 4), qh = Math.Max(1, Height / 4);
        _bloomA = CreateTarget(qw, qh);
        _bloomB = CreateTarget(qw, qh);
    }

    private RenderTarget CreateTarget(int w, int h)
    {
        var tex = _device.CreateTexture2D(new Texture2DDescription(HdrFormat, (uint)w, (uint)h, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        return new RenderTarget(tex, _device.CreateRenderTargetView(tex), _device.CreateShaderResourceView(tex), w, h);
    }

    private void ReleaseTargets()
    {
        _backRtv?.Dispose(); _backRtv = null;
        _scene?.Dispose(); _scene = null;
        _bloomA?.Dispose(); _bloomA = null;
        _bloomB?.Dispose(); _bloomB = null;
    }

    private void EnsureLevels(int count)
    {
        if (count <= _levelsCapacity) return;
        _levelsView?.Dispose();
        _levels?.Dispose();
        _levelsCapacity = Math.Max(count, 256);
        _levels = _device.CreateBuffer(new BufferDescription(
            (uint)(_levelsCapacity * 16), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured, 16));
        _levelsView = _device.CreateShaderResourceView(_levels,
            new ShaderResourceViewDescription(_levels, Format.Unknown, 0, (uint)_levelsCapacity));
    }

    private unsafe void EnsureWaterfall(int columns)
    {
        columns = Math.Max(1, columns);
        if (_waterfall is not null && _waterfallColumns == columns) return;
        _waterfallView?.Dispose();
        _waterfall?.Dispose();
        _waterfallColumns = columns;
        _waterfallHead = 0;
        var zeros = new float[columns * WaterfallRows];
        ID3D11Texture2D tex;
        fixed (float* p = zeros)
        {
            tex = _device.CreateTexture2D(
                new Texture2DDescription(Format.R32_Float, (uint)columns, WaterfallRows, 1, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)p, (uint)(columns * 4)));
        }
        _waterfall = tex;
        _waterfallView = _device.CreateShaderResourceView(tex);
    }

    /// <summary>Texture v of the newest waterfall row (for <see cref="FrameConstants.WaterfallHead"/>).</summary>
    public float WaterfallHeadV => (_waterfallHead + 0.5f) / WaterfallRows;

    /// <summary>
    /// Draws one frame and presents on vsync. <paramref name="waterfallRow"/> (one value per
    /// column, 0..1) is appended to the spectrogram when not empty. Returns false when the device
    /// was lost and must be recreated.
    /// </summary>
    public unsafe bool Draw(int skinShader, ReadOnlySpan<Vector4> levels, ref FrameConstants frame,
        float bloomIntensity, float bloomThreshold, float vignette, ReadOnlySpan<float> waterfallRow)
    {
        EnsureLevels(Math.Max(1, levels.Length));
        EnsureWaterfall(Math.Max(1, waterfallRow.Length > 0 ? waterfallRow.Length : _waterfallColumns));

        MappedSubresource m = _context.Map(_levels!, 0, MapMode.WriteDiscard);
        levels.CopyTo(new Span<Vector4>((void*)m.DataPointer, _levelsCapacity));
        _context.Unmap(_levels!, 0);

        if (!waterfallRow.IsEmpty)
        {
            _waterfallHead = (_waterfallHead + 1) % WaterfallRows;
            var box = new Box(0, _waterfallHead, 0, waterfallRow.Length, _waterfallHead + 1, 1);
            _context.UpdateSubresource(waterfallRow, _waterfall!, 0, (uint)(waterfallRow.Length * 4), 0, box);
        }
        frame.WaterfallHead = WaterfallHeadV;
        SetFrame(ref frame);

        // 1. Skin into the HDR scene.
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetConstantBuffer(0, _frameCb);
        _context.PSSetConstantBuffer(1, _postCb);
        _context.PSSetSampler(0, _wrap);
        _context.PSSetSampler(1, _clamp);
        _context.PSSetShaderResource(0, _levelsView!);
        _context.PSSetShaderResource(1, _waterfallView!);
        Pass(_skins[Math.Clamp(skinShader, 0, 3)], _scene!.Rtv, _scene.Width, _scene.Height);

        // 2. Bloom: bright pass to quarter size, then two separable blur iterations.
        if (bloomIntensity > 0)
        {
            SetPost(_bloomA!, _scene, bloomIntensity, bloomThreshold, vignette, 1);
            Pass(_bright, _bloomA!.Rtv, _bloomA.Width, _bloomA.Height, _scene.Srv);
            for (int i = 0; i < 2; i++)
            {
                float spread = i == 0 ? 1.0f : 2.0f;
                SetPost(_bloomB!, _bloomA, bloomIntensity, bloomThreshold, vignette, spread);
                Pass(_blurH, _bloomB!.Rtv, _bloomB.Width, _bloomB.Height, _bloomA.Srv);
                SetPost(_bloomA, _bloomB, bloomIntensity, bloomThreshold, vignette, spread);
                Pass(_blurV, _bloomA.Rtv, _bloomA.Width, _bloomA.Height, _bloomB.Srv);
            }
        }

        // 3. Composite into the back buffer.
        var post = new PostConstants
        {
            TargetSize = new Vector2(Width, Height),
            SourceTexel = new Vector2(1f / _bloomA!.Width, 1f / _bloomA.Height),
            BloomIntensity = bloomIntensity,
            Threshold = bloomThreshold,
            Vignette = vignette,
            BlurSpread = 1,
        };
        WritePost(ref post);
        _context.PSSetShaderResource(3, _bloomA.Srv);
        Pass(_composite, _backRtv!, Width, Height, _scene.Srv);
        _context.PSSetShaderResources(0, 4, _nullViews!);

        var hr = _swapChain.Present(1, PresentFlags.None);
        return hr != Vortice.DXGI.ResultCode.DeviceRemoved && hr != Vortice.DXGI.ResultCode.DeviceReset;
    }

    private void Pass(ID3D11PixelShader ps, ID3D11RenderTargetView target, int w, int h, ID3D11ShaderResourceView? source = null)
    {
        // Unbind the source slot first so a texture is never bound as input and output at once.
        _context.PSSetShaderResources(2, 1, _nullViews!);
        _context.OMSetRenderTargets(target);
        _context.RSSetViewport(new Viewport(0, 0, w, h));
        if (source is not null) _context.PSSetShaderResource(2, source);
        _context.PSSetShader(ps);
        _context.Draw(3, 0);
    }

    private unsafe void SetFrame(ref FrameConstants frame)
    {
        MappedSubresource c = _context.Map(_frameCb, 0, MapMode.WriteDiscard);
        *(FrameConstants*)c.DataPointer = frame;
        _context.Unmap(_frameCb, 0);
    }

    private void SetPost(RenderTarget target, RenderTarget source, float intensity, float threshold, float vignette, float spread)
    {
        var p = new PostConstants
        {
            TargetSize = new Vector2(target.Width, target.Height),
            SourceTexel = new Vector2(1f / source.Width, 1f / source.Height),
            BloomIntensity = intensity,
            Threshold = threshold,
            Vignette = vignette,
            BlurSpread = spread,
        };
        WritePost(ref p);
    }

    private unsafe void WritePost(ref PostConstants p)
    {
        // Unbind the target textures from the output before writing, then update the constants.
        MappedSubresource c = _context.Map(_postCb, 0, MapMode.WriteDiscard);
        *(PostConstants*)c.DataPointer = p;
        _context.Unmap(_postCb, 0);
    }

    private static string LoadShaderSource()
    {
        using Stream s = typeof(D3DSpectrumRenderer).Assembly.GetManifestResourceStream("nSpectrum.Wpf.Spectrum.hlsl")
            ?? throw new InvalidOperationException("Shader resource missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        _context.ClearState();
        _context.Flush();
        ReleaseTargets();
        _waterfallView?.Dispose();
        _waterfall?.Dispose();
        _levelsView?.Dispose();
        _levels?.Dispose();
        _wrap.Dispose();
        _clamp.Dispose();
        _frameCb.Dispose();
        _postCb.Dispose();
        foreach (var s in _skins) s.Dispose();
        _bright.Dispose(); _blurH.Dispose(); _blurV.Dispose(); _composite.Dispose();
        _vs.Dispose();
        if (_waitHandle != 0) NativeMethods.CloseHandle(_waitHandle);
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
