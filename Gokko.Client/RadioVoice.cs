using Gokko.Common;
using NAudio.Wave;

namespace Gokko.Client;

/// <summary>2次のフィルター（高い音・低い音を削る）。</summary>
internal sealed class Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad HighPass(float freq, float rate, float q = 0.707f) => Make(freq, rate, q, true);
    public static Biquad LowPass(float freq, float rate, float q = 0.707f) => Make(freq, rate, q, false);

    /// <summary>ある周波数のあたりだけ持ち上げる（dB）。</summary>
    public static Biquad Peaking(float freq, float rate, float q, float db)
    {
        var w = 2 * Math.PI * freq / rate;
        var alpha = Math.Sin(w) / (2 * q);
        var cos = Math.Cos(w);
        var a = Math.Pow(10, db / 40);
        var a0 = 1 + alpha / a;
        return new Biquad
        {
            _b0 = (float)((1 + alpha * a) / a0), _b1 = (float)(-2 * cos / a0), _b2 = (float)((1 - alpha * a) / a0),
            _a1 = (float)(-2 * cos / a0), _a2 = (float)((1 - alpha / a) / a0)
        };
    }

    private static Biquad Make(float freq, float rate, float q, bool high)
    {
        var w = 2 * Math.PI * freq / rate;
        var alpha = Math.Sin(w) / (2 * q);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        var f = new Biquad();
        if (high)
        {
            f._b0 = (float)((1 + cos) / 2 / a0);
            f._b1 = (float)(-(1 + cos) / a0);
        }
        else
        {
            f._b0 = (float)((1 - cos) / 2 / a0);
            f._b1 = (float)((1 - cos) / a0);
        }

        f._b2 = f._b0;
        f._a1 = (float)(-2 * cos / a0);
        f._a2 = (float)((1 - alpha) / a0);
        return f;
    }

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}

/// <summary>
/// 電話の帯域（300〜3400Hz）。受話器らしく 2kHz あたりを少し持ち上げ、ごく軽く歪ませる。
/// 内線電話の受けた声と、回線の向こうから聞こえる音（呼出中の音・保留音など）に使う。Web（voicefx.js）と同じ。
/// </summary>
internal sealed class PhoneBand
{
    private const float Rate = TrainRadioFormat.SampleRate;
    private const float Drive = 1.8f;
    private static readonly float Norm = (float)Math.Tanh(Drive);
    private readonly Biquad _hp1 = Biquad.HighPass(300, Rate), _hp2 = Biquad.HighPass(300, Rate);
    private readonly Biquad _peak = Biquad.Peaking(2000, Rate, 0.9f, 4);
    private readonly Biquad _lp1 = Biquad.LowPass(3400, Rate), _lp2 = Biquad.LowPass(3400, Rate);

    public float Process(float x)
    {
        x = _lp2.Process(_lp1.Process(_peak.Process(_hp2.Process(_hp1.Process(x)))));
        return (float)Math.Tanh(x * Drive) / Norm * 0.9f;
    }
}

/// <summary>
/// 話している人1人分の受話の音。届いた音声をためて流し、人ごとの音量と無線風の音をかける。
/// 無線風: 声の高い音と低い音を削り（300〜3000Hz）、軽く歪ませる（雑音は混ぜない）。
/// </summary>
internal sealed class RadioVoice : ISampleProvider
{
    private const int Rate = TrainRadioFormat.SampleRate;
    private static readonly WaveFormat Pcm = new(Rate, 16, 1);

    private readonly BufferedWaveProvider _buffer = new(Pcm)
    {
        BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true, ReadFully = false
    };

    private readonly Biquad _hp1 = Biquad.HighPass(300, Rate), _hp2 = Biquad.HighPass(300, Rate);
    private readonly Biquad _lp1 = Biquad.LowPass(3000, Rate), _lp2 = Biquad.LowPass(3000, Rate);
    private readonly byte[] _bytes = new byte[Rate * 2];
    private float _peak;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    /// <summary>人ごとの音量 × 受話音量 × 防護受報中の下げ。</summary>
    public volatile float Gain = 1f;

    public volatile bool Effect = true;
    public volatile float Strength = 0.6f;

    /// <summary>内線電話の声: 無線風の代わりに電話の帯域にする（<see cref="Effect"/> は見ない）。</summary>
    public PhoneBand? Phone { get; init; }

    /// <summary>最後に音声が届いた時刻（Environment.TickCount64）。</summary>
    public long LastPacketTick { get; private set; } = Environment.TickCount64;

    /// <summary>今の大きさ（受話メーター用。読むと少しずつ下がる）。</summary>
    public float TakePeak()
    {
        var p = _peak;
        _peak *= 0.6f;
        return p;
    }

    private readonly RadioDecoder _decoder = new();

    /// <summary>届いた Opus のパケット1つを展開してためる。壊れたパケットは捨てる。</summary>
    public void AddOpus(byte[] opus)
    {
        var pcm = _decoder.Decode(opus);
        if (pcm != null) Add(pcm);
    }

    private void Add(byte[] pcm)
    {
        LastPacketTick = Environment.TickCount64;
        _buffer.AddSamples(pcm, 0, pcm.Length);
        // 回線の遅れで古い音が 200ms を超えてたまったら、古い方から捨てて 120ms まで戻す（全部は捨てない。声が大きく欠けないように）
        var excess = _buffer.BufferedBytes - KeepBytes;
        if (_buffer.BufferedBytes > MaxBytes && excess > 0) _buffer.Read(_discard, 0, Math.Min(excess & ~1, _discard.Length));
    }

    private const int MaxBytes = Rate * 2 * 200 / 1000; // 200ms
    private const int KeepBytes = Rate * 2 * 120 / 1000; // 120ms
    private readonly byte[] _discard = new byte[Rate * 2];
    private static readonly TimeSpan StartBuffer = TimeSpan.FromMilliseconds(80);
    private bool _playing;

    public int Read(float[] buffer, int offset, int count)
    {
        // 話し始めは 80ms ためてから流す（回線のゆらぎで、すぐ途切れないように）。それ以上届かないなら、たまった分を流す
        if (!_playing && _buffer.BufferedBytes > 0
            && (_buffer.BufferedDuration >= StartBuffer || Environment.TickCount64 - LastPacketTick > 120))
            _playing = true;

        var bytes = Math.Min(count * 2, _bytes.Length);
        var got = _playing ? _buffer.Read(_bytes, 0, bytes) / 2 : 0;
        if (_playing && got < count) _playing = false; // 使い切った。次はまた、ためてから流す
        var gain = Gain;
        var effect = Effect;
        var strength = Math.Clamp(Strength, 0, 1);
        var drive = 1 + 5 * strength;
        var norm = (float)Math.Tanh(drive);

        for (var i = 0; i < count; i++)
        {
            float x = i < got ? BitConverter.ToInt16(_bytes, i * 2) / 32768f : 0f;
            if (Phone != null) x = Phone.Process(x);
            else if (effect)
            {
                x = _lp2.Process(_lp1.Process(_hp2.Process(_hp1.Process(x))));
                x = (float)Math.Tanh(x * drive) / norm * 0.9f;
            }

            x *= gain;
            var a = Math.Abs(x);
            if (a > _peak) _peak = a;
            buffer[offset + i] = Math.Clamp(x, -1f, 1f);
        }

        return count; // いつも満たして返す（ミキサーから外れないように）
    }

}

/// <summary>
/// 呼出音（同梱の「ピン・ポン」。無音を含めて約2.7秒をくり返す）。
/// 司令員には応答待ちの呼出があるとき、運転士・車掌には司令員からの個別呼出のとき鳴らす。
/// Gain が 0 の間は止まり、次に鳴らすときは頭から。
/// </summary>
internal sealed class RingTone : ISampleProvider
{
    private static readonly float[] Samples = ChimeTone.Load("private_call_chime.wav");
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(TrainRadioFormat.SampleRate, 1);
    public volatile float Gain;

    public int Read(float[] buffer, int offset, int count)
    {
        var gain = Gain;
        if (gain == 0 || Samples.Length == 0)
        {
            _position = 0;
            Array.Clear(buffer, offset, count);
            return count;
        }

        for (var i = 0; i < count; i++)
        {
            buffer[offset + i] = Samples[_position] * gain;
            if (++_position >= Samples.Length) _position = 0;
        }

        return count;
    }
}

/// <summary>一斉通告の開始チャイム（同梱の「ピン・ポン・パーン」。約3.6秒）。<see cref="Start"/> で頭から1回鳴らす。</summary>
internal sealed class ChimeTone : ISampleProvider
{
    private static readonly float[] Samples = Load("broadcast_chime.wav");
    private int _position = int.MaxValue;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(TrainRadioFormat.SampleRate, 1);
    public volatile float Gain;

    /// <summary>EXE に埋め込んだ WAV を 16kHz・モノラルの float で読む。</summary>
    internal static float[] Load(string file)
    {
        try
        {
            using var stream = typeof(ChimeTone).Assembly.GetManifestResourceStream("Gokko.Client.Audio." + file);
            if (stream == null) return [];
            using var reader = new WaveFileReader(stream);
            ISampleProvider s = reader.ToSampleProvider();
            if (s.WaveFormat.Channels == 2) s = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(s);
            if (s.WaveFormat.SampleRate != TrainRadioFormat.SampleRate) s = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(s, TrainRadioFormat.SampleRate);
            var list = new List<float>();
            var buf = new float[4096];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.AsSpan(0, n).ToArray());
            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>頭から鳴らす。</summary>
    public void Start() => _position = 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var gain = Gain;
        for (var i = 0; i < count; i++)
        {
            var p = _position;
            buffer[offset + i] = p < Samples.Length ? Samples[p] * gain : 0f;
            if (p < Samples.Length) _position = p + 1;
        }

        return count; // いつも満たして返す（ミキサーから外れないように）
    }
}
