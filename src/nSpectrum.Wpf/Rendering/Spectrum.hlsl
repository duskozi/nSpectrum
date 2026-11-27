// nSpectrum skins and post-processing.
//
// Every skin is a single full-screen triangle: the pixel shader decides per pixel whether it is a
// lit LED, an unlit LED, a bar, a curve, grid or background, so the CPU cost does not depend on
// how many bars or LEDs are drawn. Skins render into an HDR scene target; lit elements go above
// 1.0 so the bloom passes pick them up and give the real-LED glow.
//
// Coordinates come from SV_Position (pixels), never from interpolated UVs, so the same code is
// correct with Direct3D's top-left and OpenGL's bottom-left origins (used by the preview tool).

cbuffer Frame : register(b0)
{
    float2 ViewSize;      // pixels of the scene target
    float  Time;          // seconds
    uint   Columns;       // bars, bands or pixel columns
    float  Rows;          // LED rows (rack, matrix)
    float  CellGap;       // gap between LED rows, fraction of a row
    float  BarGap;        // gap between columns, fraction of a column
    float  PeakThickness; // pixels (curves)
    float  DbGridStep;    // grid spacing, fraction of the level range
    float  GridOffset;    // first grid line, fraction of the level range from the bottom
    float  SplitY;        // spectrogram: fraction of the height used by the spectrum on top
    float  WaterfallHead; // spectrogram: texture v of the newest row
    float  ZoneYellow;    // level fraction where LEDs turn from low to mid colour
    float  ZoneRed;       // level fraction where LEDs turn from mid to high colour
    float  Glow;          // 0..1, extra emission of lit elements
    float  FillAlpha;     // bars-and-curves: opacity of the fill
    float4 ColorLow;
    float4 ColorMid;
    float4 ColorHigh;
    float4 PeakColor;
    float4 CurveColor;
    float4 Background0;   // top
    float4 Background1;   // bottom
    float4 GridColor;
    float4 UnlitColor;    // rgb tint and a = brightness of unlit LEDs
    float4 FreqGrid[8];   // up to 32 x positions in pixels, < 0 = unused
};

cbuffer Post : register(b1)
{
    float2 TargetSize;    // pixels of the pass's render target
    float2 SourceTexel;   // 1 / size of the source texture
    float  BloomIntensity;
    float  Threshold;
    float  Vignette;
    float  BlurSpread;
};

// x = bar level, y = peak cap, z = RMS curve, w = peak curve; all 0..1 of the level range.
StructuredBuffer<float4> Levels : register(t0);
Texture2D<float> Waterfall : register(t1);
Texture2D<float4> Source : register(t2);
Texture2D<float4> BloomTex : register(t3);
SamplerState WrapSampler : register(s0);
SamplerState ClampSampler : register(s1);

struct VSOut
{
    float4 pos : SV_Position;
};

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// ---------------------------------------------------------------- helpers

float Hash(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

float3 Ramp(float t)
{
    t = saturate(t);
    return t < 0.5 ? lerp(ColorLow.rgb, ColorMid.rgb, t * 2) : lerp(ColorMid.rgb, ColorHigh.rgb, t * 2 - 1);
}

float3 ZoneColor(float level)
{
    return level < ZoneYellow ? ColorLow.rgb : (level < ZoneRed ? ColorMid.rgb : ColorHigh.rgb);
}

float3 Backdrop(float2 p, float h)
{
    float yUp = 1 - p.y / ViewSize.y;
    float3 c = lerp(Background1.rgb, Background0.rgb, yUp);
    // A soft light pool behind the spectrum, like a lit glass panel.
    float2 d = (p - float2(ViewSize.x * 0.5, ViewSize.y * 0.15)) / ViewSize;
    c += Background0.rgb * 0.6 * exp(-dot(d, d) * 5.0);
    return c;
}

// Anti-aliased grid: horizontal lines from the dB grid, vertical lines from FreqGrid.
float3 Grid(float3 c, float2 p, float yUpPx, float heightPx)
{
    if (DbGridStep > 0)
    {
        float g = (yUpPx / heightPx - GridOffset) / DbGridStep;
        float dist = abs(g - round(g)) * DbGridStep * heightPx;
        c = lerp(c, GridColor.rgb, GridColor.a * saturate(1 - dist));
    }
    [unroll] for (int k = 0; k < 32; k++)
    {
        float gx = FreqGrid[k >> 2][k & 3];
        if (gx >= 0)
            c = lerp(c, GridColor.rgb, GridColor.a * saturate(1 - abs(p.x - gx)));
    }
    return c;
}

// Distance in pixels from p to a per-column curve (component sel of Levels), slope-corrected.
float CurveDistance(float x, float yUpPx, float heightPx, int sel)
{
    float fx = clamp(x - 0.5, 0, (float)Columns - 1.001);
    uint c0 = (uint)fx;
    uint c1 = min(c0 + 1, Columns - 1);
    float t = fx - c0;
    float v0 = Levels[c0][sel], v1 = Levels[c1][sel];
    float y = lerp(v0, v1, t) * heightPx;
    float slope = (v1 - v0) * heightPx;
    return abs(yUpPx - y) / sqrt(1 + slope * slope);
}

// ---------------------------------------------------------------- skins

// Bars and curves: translucent gradient fill, a bright rim, a white RMS curve and a gold peak curve.
float3 Aurora(float2 p, float heightPx, float yUpPx, float3 c)
{
    float colW = ViewSize.x / Columns;
    uint col = min((uint)(p.x / colW), Columns - 1);
    float4 L = Levels[col];
    float barPx = L.x * heightPx;

    if (yUpPx <= barPx && L.x > 0)
    {
        float3 g = Ramp(yUpPx / heightPx);
        float depth = saturate(yUpPx / max(barPx, 1));       // 0 at the floor, 1 at the bar top
        float3 fill = g * (0.25 + 0.75 * depth * depth);
        c = lerp(c, fill, FillAlpha);
        float rim = saturate(1 - (barPx - yUpPx) / 2.5);      // 2-3 px bright top edge
        c += g * rim * (0.6 + 1.6 * Glow);
    }

    float dRms = CurveDistance(p.x / colW, yUpPx, heightPx, 2);
    c = lerp(c, CurveColor.rgb * (1 + 1.5 * Glow), CurveColor.a * saturate(PeakThickness + 0.5 - dRms));

    float dPeak = CurveDistance(p.x / colW, yUpPx, heightPx, 3);
    c = lerp(c, PeakColor.rgb * (1 + 2.0 * Glow), PeakColor.a * saturate(PeakThickness - dPeak));
    return c;
}

// Segmented LED bars (rack) or round LEDs (matrix), with visible unlit LEDs and peak caps.
float3 Leds(float2 p, float heightPx, float yUpPx, float3 c, bool isRound)
{
    float colW = ViewSize.x / Columns;
    float rowH = heightPx / Rows;
    uint col = min((uint)(p.x / colW), Columns - 1);
    float row = floor(yUpPx / rowH);
    if (row < 0 || row >= Rows) return c;

    float2 local = float2(p.x - (col + 0.5) * colW, yUpPx - (row + 0.5) * rowH); // from cell centre
    float4 L = Levels[col];
    float rowLevel = (row + 0.5) / Rows;
    bool lit = rowLevel < L.x;
    bool peak = L.y > 0.0001 && row == min(floor(L.y * Rows - 0.0001), Rows - 1) && !lit;
    float3 zone = ZoneColor(rowLevel);

    float coverage, shade;
    if (isRound)
    {
        float r = 0.40 * min(colW * (1 - BarGap), rowH * (1 + CellGap));
        float d = length(local) - r;
        coverage = saturate(0.5 - d);
        float2 hl = (local - float2(-0.30, 0.32) * r) / (0.45 * r);
        shade = 0.70 + 0.30 * saturate(1 - length(local) / r) + 0.9 * exp(-dot(hl, hl) * 2.0);
    }
    else
    {
        float2 halfSize = float2(colW * (1 - BarGap), rowH * (1 - CellGap)) * 0.5;
        float radius = min(1.5, min(halfSize.x, halfSize.y));
        float2 q = abs(local) - halfSize + radius;
        float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
        coverage = saturate(0.5 - d);
        float v = local.y / max(halfSize.y, 1);                   // -1 bottom .. 1 top of the segment
        shade = 0.80 + 0.35 * saturate(v * 0.5 + 0.5);
    }

    float3 led;
    if (peak)
        led = PeakColor.rgb * (1.2 + 1.0 * Glow) * shade;
    else if (lit)
        led = zone * (1.0 + 0.9 * Glow) * shade;
    else
        led = lerp(UnlitColor.rgb, zone, 0.35) * UnlitColor.a * shade;
    return lerp(c, led, coverage);
}

// Inferno-like colour map of our own: black, indigo, violet, magenta, orange, yellow, white.
float3 Heat(float t)
{
    t = saturate(t);
    float3 c0 = float3(0.00, 0.00, 0.02), c1 = float3(0.10, 0.03, 0.30), c2 = float3(0.42, 0.06, 0.55);
    float3 c3 = float3(0.85, 0.16, 0.42), c4 = float3(1.00, 0.52, 0.10), c5 = float3(1.00, 0.88, 0.30), c6 = float3(1.0, 1.0, 0.92);
    float s = t * 6;
    if (s < 1) return lerp(c0, c1, s);
    if (s < 2) return lerp(c1, c2, s - 1);
    if (s < 3) return lerp(c2, c3, s - 2);
    if (s < 4) return lerp(c3, c4, s - 3);
    if (s < 5) return lerp(c4, c5, s - 4);
    return lerp(c5, c6, s - 5);
}

float4 SceneCommon(float2 p, int skin)
{
    float h = ViewSize.y;
    float3 c = Backdrop(p, h);

    if (skin == 3)
    {
        // Spectrogram: spectrum on top, waterfall below (newest row at the top, flowing down).
        float splitPx = SplitY * h;
        float yFromTop = p.y;
        if (yFromTop < splitPx)
        {
            float yUpPx = splitPx - yFromTop;
            c = Grid(c, p, yUpPx, splitPx);
            c = Aurora(p, splitPx, yUpPx, c);
        }
        else
        {
            float t = (yFromTop - splitPx) / max(h - splitPx, 1);
            float v = WaterfallHead - t;
            float level = Waterfall.SampleLevel(WrapSampler, float2(p.x / ViewSize.x, v), 0);
            c = Heat(level) * (1 + 0.8 * Glow * saturate(level * 1.4 - 0.4));
            c = lerp(c, float3(0.6, 0.65, 0.7), 0.6 * saturate(1 - abs(yFromTop - splitPx)));
        }
        return float4(c, 1);
    }

    float yUpPx = h - p.y;
    if (skin == 0)
    {
        c = Grid(c, p, yUpPx, h);
        c = Aurora(p, h, yUpPx, c);
    }
    else
    {
        // LEDs: a faint glass reflection across the top third.
        float refl = saturate((yUpPx / h - 0.62) * 3) * saturate((0.98 - yUpPx / h) * 8);
        c += refl * 0.025;
        if (GridColor.a > 0) c = Grid(c, p, yUpPx, h); // themes with a grid behind the LEDs (rack skin)
        c = Leds(p, h, yUpPx, c, skin == 2);
    }
    return float4(c, 1);
}

float4 PS_Aurora(VSOut i) : SV_Target { return SceneCommon(i.pos.xy, 0); }
float4 PS_Rack(VSOut i) : SV_Target { return SceneCommon(i.pos.xy, 1); }
float4 PS_Matrix(VSOut i) : SV_Target { return SceneCommon(i.pos.xy, 2); }
float4 PS_Spectrogram(VSOut i) : SV_Target { return SceneCommon(i.pos.xy, 3); }

// ---------------------------------------------------------------- post-processing

// Bright pass into a quarter-size target: 4 bilinear taps cover a 4x4 block of the scene.
float4 PS_Bright(VSOut i) : SV_Target
{
    float2 uv = i.pos.xy / TargetSize;
    float3 s = 0;
    s += Source.SampleLevel(ClampSampler, uv + SourceTexel * float2(-1, -1), 0).rgb;
    s += Source.SampleLevel(ClampSampler, uv + SourceTexel * float2(1, -1), 0).rgb;
    s += Source.SampleLevel(ClampSampler, uv + SourceTexel * float2(-1, 1), 0).rgb;
    s += Source.SampleLevel(ClampSampler, uv + SourceTexel * float2(1, 1), 0).rgb;
    s *= 0.25;
    float lum = max(s.r, max(s.g, s.b));
    float knee = saturate((lum - Threshold) / max(lum, 1e-4));
    return float4(s * knee, 1);
}

float4 Blur(float2 p, float2 dir)
{
    float2 uv = p / TargetSize;
    float2 o1 = dir * SourceTexel * 1.3846153846 * BlurSpread;
    float2 o2 = dir * SourceTexel * 3.2307692308 * BlurSpread;
    float3 c = Source.SampleLevel(ClampSampler, uv, 0).rgb * 0.2270270270;
    c += Source.SampleLevel(ClampSampler, uv + o1, 0).rgb * 0.3162162162;
    c += Source.SampleLevel(ClampSampler, uv - o1, 0).rgb * 0.3162162162;
    c += Source.SampleLevel(ClampSampler, uv + o2, 0).rgb * 0.0702702703;
    c += Source.SampleLevel(ClampSampler, uv - o2, 0).rgb * 0.0702702703;
    return float4(c, 1);
}

float4 PS_BlurH(VSOut i) : SV_Target { return Blur(i.pos.xy, float2(1, 0)); }
float4 PS_BlurV(VSOut i) : SV_Target { return Blur(i.pos.xy, float2(0, 1)); }

float3 SoftClip(float3 c)
{
    // Linear up to 0.8, then rolls off smoothly toward 1 so hot LEDs saturate like real ones.
    float3 over = max(c - 0.8, 0);
    return min(c, 0.8) + 0.2 * (1 - exp(-over / 0.2));
}

float4 PS_Composite(VSOut i) : SV_Target
{
    float2 uv = i.pos.xy / TargetSize;
    float3 c = Source.SampleLevel(ClampSampler, uv, 0).rgb;
    c += BloomTex.SampleLevel(ClampSampler, uv, 0).rgb * BloomIntensity;
    float2 d = uv - 0.5;
    c *= 1 - Vignette * dot(d, d) * 1.8;
    c = SoftClip(c);
    c += (Hash(i.pos.xy) - 0.5) / 255.0; // dither: no banding in the dark gradients
    return float4(c, 1);
}
