using System.Threading.Channels;
using Gokko.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Gokko.Client;

/// <summary>
/// 内線電話の音（列車無線とは別に動く）。話している通話があるあいだだけマイクを開いて送り、相手の声を流す。
/// 呼出音・呼出中の音（リングバック）・保留音・キャッチホンの音・ボタンの音は <see cref="PhoneTones"/> で作る。
/// マイクの選び方・エコー除去・感度・ノイズゲートは、列車無線の［音の設定］と同じものを使う。
/// </summary>
public sealed class ExtensionAudio : IDisposable
{
    private const int Rate = TrainRadioFormat.SampleRate;
    private static readonly TimeSpan GateHold = TimeSpan.FromMilliseconds(400);

    private readonly ServerLink _server;
    private readonly RadioSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, RadioVoice> _voices = new();
    private readonly PhoneTones _tones = new();
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;
    private IRadioMicrophone? _mic;
    private Channel<byte[]>? _frames;
    private CancellationTokenSource? _sendCancel;
    private long _micRetryTick, _outputRetryTick, _gateOpenUntil;
    private volatile ExtensionCall? _talking;
    private volatile ExtensionState? _state;
    private volatile bool _online, _disposed, _transmitting;
    private volatile float _volume = 1f;
    private volatile string _error = "";

    public string Error => _error;
    public bool Transmitting => _transmitting;
    public bool Muted { get; set; }

    /// <summary>卓上の電話機で受話器を上げて、まだ番号を押していないとき（内線発信音を鳴らす）。</summary>
    public bool DialTone { get; set; }

    /// <summary>受話と呼出音の音量（0〜2）。</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 2);
    }

    public ExtensionAudio(ServerLink server, RadioSettings settings)
    {
        _server = server;
        _settings = settings;
        _server.ExtensionAudioReceived += Receive;
    }

    /// <summary>ボタンの音（DTMF）。</summary>
    public void Key(char key) => _tones.Key(key);

    /// <summary>電話機の画面から 100ms ごとに呼ぶ。状態に合わせて音とマイクをそろえる。</summary>
    public void Synchronize(ExtensionState? state, bool online, bool keitai)
    {
        if (_disposed) return;
        _state = online ? state : null;
        _online = online;
        EnsureOutput();

        var calls = _state?.Calls ?? new();
        var active = calls.FirstOrDefault(c => c.Status is "talking" or "outgoing");
        var incoming = calls.FirstOrDefault(c => c.Status == "incoming");
        _talking = active is { Status: "talking" } ? active : null;

        _tones.Volume = _volume;
        _tones.Keitai = keitai;
        _tones.Ring = incoming != null && active == null && _state?.CanUse == true && _state?.HandsetLinked != true;
        _tones.Waiting = incoming != null && active != null && _state?.HandsetLinked != true;
        _tones.RingBack = active is { Status: "outgoing" } && _state?.HandsetLinked != true;
        _tones.HoldMusic = active is { Status: "talking", PeerHeld: true } && _state?.HandsetLinked != true;
        _tones.Dial = DialTone && active == null && incoming == null && _state?.CanUse == true && _state?.HandsetLinked != true;
        _tones.Line = active is { Status: "talking", PeerHeld: false } && _state?.HandsetLinked != true; // 話している間だけ回線の「サー」

        // 話している通話の相手だけ残す
        var parties = _talking?.Parties.Select(p => p.Id).ToHashSet() ?? new HashSet<string>();
        lock (_gate)
        {
            foreach (var (id, voice) in _voices.ToList())
            {
                if (!parties.Contains(id) && Environment.TickCount64 - voice.LastPacketTick > 1000)
                {
                    _mixer?.RemoveMixerInput(voice);
                    _voices.Remove(id);
                    continue;
                }

                voice.Gain = _volume;
            }
        }

        var want = online && _talking != null && _state?.HandsetLinked != true && !Muted;
        if (want && _mic == null && Environment.TickCount64 >= _micRetryTick) StartMicrophone();
        if (!want && _mic != null) StopMicrophone();
        if (!want) _transmitting = false;
    }

    private void EnsureOutput()
    {
        if (_output != null || Environment.TickCount64 < _outputRetryTick) return;
        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)) { ReadFully = true };
            _mixer.AddMixerInput(_tones);
            _output = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 120, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception ex)
        {
            _error = "受話の音を出せません: " + ex.Message;
            _outputRetryTick = Environment.TickCount64 + 5000;
            try { _output?.Dispose(); } catch { }
            _output = null;
            _mixer = null;
        }
    }

    private void StartMicrophone()
    {
        try
        {
            var mic = RadioMicrophone.Open(_settings.EchoCancel, _settings.InputDeviceId ?? "", out var note);
            _error = note;
            var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
            var cancel = new CancellationTokenSource();
            var encoder = RadioCodec.CreateEncoder();
            mic.Frame += pcm => OnFrame(pcm, encoder, frames);
            mic.Failed += message =>
            {
                _error = message;
                _micRetryTick = Environment.TickCount64 + 3000;
                StopMicrophone();
            };
            _frames = frames;
            _sendCancel = cancel;
            _mic = mic;
            _ = SendLoopAsync(frames, cancel.Token);
        }
        catch (Exception ex)
        {
            _error = "マイクを開けません: " + ex.Message + "（列車無線と同時に使うと開けないことがあります）";
            _micRetryTick = Environment.TickCount64 + 3000;
        }
    }

    private void StopMicrophone()
    {
        var mic = _mic;
        _mic = null;
        _transmitting = false;
        _sendCancel?.Cancel();
        _frames?.Writer.TryComplete();
        _sendCancel = null;
        _frames = null;
        if (mic == null) return;
        try { mic.Dispose(); } catch { }
    }

    private void OnFrame(byte[] pcm, RadioEncoder encoder, Channel<byte[]> frames)
    {
        var gain = Math.Clamp(_settings.MicGain, 0.25f, TrainRadioAudio.MaxMicGain);
        var n = pcm.Length / 2;
        var samples = new short[n];
        var peak = 0f;
        for (var i = 0; i < n; i++)
        {
            var x = BitConverter.ToInt16(pcm, i * 2) / 32768f * gain;
            peak = Math.Max(peak, Math.Abs(x));
            samples[i] = (short)Math.Clamp(x * 32767f, short.MinValue, short.MaxValue);
        }

        var now = Environment.TickCount64;
        if (peak >= _settings.Gate) _gateOpenUntil = now + (long)GateHold.TotalMilliseconds;
        _transmitting = now < _gateOpenUntil && _talking != null;
        if (!_transmitting) return;
        var packet = encoder.Encode(samples);
        if (packet != null) frames.Writer.TryWrite(packet);
    }

    private async Task SendLoopAsync(Channel<byte[]> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var opus in frames.Reader.ReadAllAsync(ct))
            {
                var call = _talking;
                if (call == null || !_online) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await _server.SendExtensionAudioAsync(new TrainRadioPacket { SessionId = call.Id, Opus = opus }, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _error = "音声を送れません: " + ex.Message;
        }
    }

    private void Receive(TrainRadioPacket packet)
    {
        if (_disposed || !_online) return;
        var call = _talking;
        var state = _state;
        if (call == null || state == null || packet.SessionId != call.Id || packet.SenderId == state.SelfId) return;
        if (packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes } || call.Parties.All(p => p.Id != packet.SenderId)) return;
        lock (_gate)
        {
            if (_mixer == null) return;
            if (!_voices.TryGetValue(packet.SenderId, out var voice))
            {
                voice = new RadioVoice { Gain = _volume, Phone = new PhoneBand() };
                _voices[packet.SenderId] = voice;
                _mixer.AddMixerInput(voice);
            }

            voice.AddOpus(packet.Opus);
        }
    }

    /// <summary>切断したとき: マイクと音を止める。</summary>
    public void Suspend()
    {
        Synchronize(null, false, _tones.Keitai);
        lock (_gate)
        {
            foreach (var v in _voices.Values) _mixer?.RemoveMixerInput(v);
            _voices.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.ExtensionAudioReceived -= Receive;
        StopMicrophone();
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
    }
}

/// <summary>
/// 電話の音（呼出音・リングバック・保留音・キャッチホン・ボタンの音）。音のファイルは使わず、ここで作る。
/// 回線の向こうから聞こえる音（リングバック・保留音・キャッチホン・発信音・回線の「サー」）は電話の帯域に通す。電話機のベルとボタンの音はそのまま。
/// </summary>
internal sealed class PhoneTones : ISampleProvider
{
    private const int Rate = TrainRadioFormat.SampleRate;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    public volatile bool Ring, RingBack, HoldMusic, Waiting, Keitai, Dial;

    /// <summary>話している間: 回線の「サー」をうっすら流す。</summary>
    public volatile bool Line;
    public volatile float Volume = 1f;

    private long _ringT, _backT, _holdT, _waitT, _dialT;
    private double _p1, _p2;
    private volatile int _keyLeft;
    private double _k1, _k2, _kp1, _kp2;
    private readonly PhoneBand _band = new();
    private readonly Random _noise = new();
    private double _hiss;

    private static readonly Dictionary<char, (int, int)> Dtmf = new()
    {
        ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477), ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477),
        ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477), ['*'] = (941, 1209), ['0'] = (941, 1336), ['#'] = (941, 1477)
    };

    // 携帯の着信音・保留音（短い旋律。オリジナル）
    private static readonly double[] KeitaiNotes = [1318.5, 1568, 2093, 1568, 1318.5, 1568, 2093, 2637];
    private static readonly (double F, double D)[] HoldTune =
        [(523.3, .3), (659.3, .3), (784, .3), (659.3, .3), (698.5, .3), (880, .3), (784, .6), (587.3, .3), (698.5, .3), (880, .3), (784, .3), (659.3, .6)];

    public void Key(char key)
    {
        if (!Dtmf.TryGetValue(key, out var f)) return;
        (_k1, _k2) = f;
        _keyLeft = Rate * 90 / 1000;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            double s = 0, line = 0;
            if (Ring)
            {
                var t = _ringT++ % (Rate * 3) / (double)Rate; // 3秒ごと
                if (Keitai)
                {
                    var idx = (int)(t / 0.13);
                    if (idx < KeitaiNotes.Length && t % 0.13 < 0.11) s += Tone(KeitaiNotes[idx], ref _p1) * 0.16;
                }
                else if (t < 1.0) s += Tone(((int)(t * 16) % 2 == 0) ? 1600 : 1300, ref _p1) * 0.16; // 1秒鳴って2秒休む
            }
            else _ringT = 0;

            if (RingBack)
            {
                var t = _backT++ % (Rate * 3) / (double)Rate;
                if (t < 1.0) line += (Tone(392, ref _p1) + Tone(408, ref _p2)) * 0.05; // 400Hz を 16Hz でゆらした呼出音
            }
            else _backT = 0;

            if (HoldMusic)
            {
                var t = _holdT++ % (int)(Rate * 4.2) / (double)Rate;
                double at = 0;
                foreach (var (f, d) in HoldTune)
                {
                    if (t < at + d)
                    {
                        if (t - at < d * 0.9) line += (Tone(f, ref _p1) + 0.5 * Tone(f * 2, ref _p2)) * 0.04;
                        break;
                    }

                    at += d;
                }
            }
            else _holdT = 0;

            if (Waiting)
            {
                var t = _waitT++ % (Rate * 4) / (double)Rate;
                if (t < 0.12 || (t >= 0.25 && t < 0.37)) line += Tone(440, ref _p2) * 0.07;
            }
            else _waitT = 0;

            if (Dial)
            {
                var t = _dialT++ % (Rate / 2) / (double)Rate;
                if (t < 0.25) line += Tone(400, ref _p2) * 0.04; // 内線発信音（ツーツーツー）
            }
            else _dialT = 0;

            if (_keyLeft > 0)
            {
                _keyLeft--;
                s += (Tone(_k1, ref _kp1) + Tone(_k2, ref _kp2)) * 0.05;
            }

            _hiss += ((Line ? 0.006 : 0) - _hiss) * 0.0003; // 入り切りはなめらかに
            line += (_noise.NextDouble() * 2 - 1) * _hiss;
            s += _band.Process((float)line);
            buffer[offset + i] = (float)(s * Volume);
        }

        return count;
    }

    private static double Tone(double freq, ref double phase)
    {
        phase += 2 * Math.PI * freq / Rate;
        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
        return Math.Sin(phase);
    }
}
