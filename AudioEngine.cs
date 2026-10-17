using System.Runtime.InteropServices;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SipSoftphone;

/// <summary>
/// Two-way G.711 PCMA audio for one RTPSession:
///   mic  -> WaveIn (16-bit mono PCM) -> AudioEncoder.EncodeAudio(PCMA) -> session.SendAudio(160) every 20 ms
///   RTP  -> session.OnAudioFrameReceived -> AudioEncoder.DecodeAudio -> BufferedWaveProvider (~100 ms jitter) -> WaveOut
/// SIPSorcery.Media.AudioEncoder verified present in SIPSorcery 10.0.17 via reflection.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int SampleRate = 8000;
    private const int FrameSamples = 160; // 20 ms @ 8 kHz
    private static readonly AudioFormat PcmaFormat = new(SDPWellKnownMediaFormatsEnum.PCMA);

    private readonly AudioEncoder _codec = new(true, true);
    private readonly object _sync = new();

    private RTPSession? _session;
    private Action<EncodedAudioFrame>? _rxHandler;

    private WaveIn? _mic;
    private WaveOut? _speaker;
    private BufferedWaveProvider? _playback;
    private bool _playStarted;
    private LinearResampler? _micResampler;
    private LinearResampler? _playResampler;
    private int _playInRate = SampleRate;

    private readonly List<short> _pending = new(FrameSamples * 4);
    private bool _running;
    private bool _micRunning;

    public bool IsRunning { get { lock (_sync) return _running; } }
    public bool IsMicRunning { get { lock (_sync) return _micRunning; } }
    public string? ActiveMicDevice { get; private set; }
    public string? ActiveSpeakerDevice { get; private set; }

    public event Action<string>? Log;

    /// <summary>Attach playback + prepare. Safe to call before answer so no early RTP is lost.</summary>
    public void Attach(RTPSession session, AudioConfig cfg)
    {
        lock (_sync)
        {
            if (_session != null) return;
            _session = session;
            _rxHandler = OnAudioFrame;
            session.OnAudioFrameReceived += _rxHandler;
            StartSpeaker(cfg);
            _running = true;
        }
        Log?.Invoke("[audio] playback attached (G.711 PCMA, 8 kHz)");
    }

    /// <summary>Start microphone capture. Call ONLY after the call is answered.</summary>
    public void StartMic(AudioConfig cfg)
    {
        lock (_sync)
        {
            if (!_running || _micRunning) return;
            StartMicInternal(cfg);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _running = false;
            StopMicInternal();
            if (_session != null && _rxHandler != null)
                _session.OnAudioFrameReceived -= _rxHandler;
            _session = null;
            _rxHandler = null;
            try { _speaker?.Stop(); } catch { }
            try { _speaker?.Dispose(); } catch { }
            _speaker = null;
            _playback = null;
            _micResampler = null;
            _playResampler = null;
            _playInRate = SampleRate;
            _pending.Clear();
        }
        Log?.Invoke("[audio] stopped, devices released");
    }

    public void Dispose() => Stop();

    private void StartSpeaker(AudioConfig cfg)
    {
        try { _speaker?.Stop(); } catch { }
        try { _speaker?.Dispose(); } catch { }

        var device = FindWaveOutDevice(cfg.SpeakerDevice);
        ActiveSpeakerDevice = device >= 0 ? WaveOut.GetCapabilities(device).ProductName : "default";

        _playback = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        _speaker = new WaveOut
        {
            DeviceNumber = device,
            BufferMilliseconds = 100,
            NumberOfBuffers = 3
        };
        _speaker.Init(_playback);
        _playStarted = false;
        Log?.Invoke($"[audio] speaker: {ActiveSpeakerDevice} (pre-buffering ~80 ms jitter buffer)");
    }

    private void StartMicInternal(AudioConfig cfg)
    {
        var device = FindWaveInDevice(cfg.MicDevice);
        int[] rates = { SampleRate, 16000, 44100 };

        foreach (var rate in rates)
        {
            WaveIn? mic = null;
            try
            {
                mic = new WaveIn
                {
                    DeviceNumber = device,
                    WaveFormat = new WaveFormat(rate, 16, 1),
                    BufferMilliseconds = 20
                };
                mic.DataAvailable += OnMicData;
                mic.RecordingStopped += OnMicStopped;
                mic.StartRecording();

                _mic = mic;
                _micResampler = rate == SampleRate ? null : new LinearResampler(rate, SampleRate);
                _pending.Clear();
                _micRunning = true;
                ActiveMicDevice = device >= 0 ? WaveIn.GetCapabilities(device).ProductName : "default";
                Log?.Invoke($"[audio] mic: {ActiveMicDevice} @ {rate} Hz -> PCMA 8 kHz");
                return;
            }
            catch (Exception ex)
            {
                if (mic != null)
                {
                    try { mic.DataAvailable -= OnMicData; } catch { }
                    try { mic.RecordingStopped -= OnMicStopped; } catch { }
                    try { mic.Dispose(); } catch { }
                }
                Log?.Invoke($"[audio] mic {rate} Hz failed: {ex.Message}");
            }
        }
        Log?.Invoke("[audio] no working microphone — call will be receive-only");
    }

    private void StopMicInternal()
    {
        _micRunning = false;
        var mic = _mic;
        _mic = null;
        if (mic == null) return;
        try { mic.DataAvailable -= OnMicData; } catch { }
        try { mic.RecordingStopped -= OnMicStopped; } catch { }
        try { mic.StopRecording(); } catch { }
        try { mic.Dispose(); } catch { }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var samples = new short[e.BytesRecorded / 2];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

        if (_micResampler != null)
            samples = _micResampler.Process(samples);
        if (samples.Length == 0) return;

        RTPSession? session;
        lock (_sync)
        {
            if (!_micRunning) return;
            session = _session;
            _pending.AddRange(samples);
            while (_pending.Count >= FrameSamples)
            {
                var frame = new short[FrameSamples];
                _pending.CopyTo(0, frame, 0, FrameSamples);
                _pending.RemoveRange(0, FrameSamples);
                SendFrame(session, frame);
            }
        }
    }

    private void SendFrame(RTPSession? session, short[] pcm)
    {
        if (session == null || session.IsClosed) return;
        try
        {
            var encoded = _codec.EncodeAudio(pcm, PcmaFormat);
            if (encoded is { Length: > 0 })
                session.SendAudio(FrameSamples, encoded);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[audio] send failed: {ex.Message}");
        }
    }

    private void OnAudioFrame(EncodedAudioFrame frame)
    {
        if (frame?.EncodedAudio == null || frame.EncodedAudio.Length == 0) return;
        try
        {
            var pcm = _codec.DecodeAudio(frame.EncodedAudio, frame.AudioFormat);
            if (pcm == null || pcm.Length == 0) return;

            var rate = frame.AudioFormat.ClockRate > 0 ? frame.AudioFormat.ClockRate : SampleRate;
            lock (_sync)
            {
                if (!_running || _playback == null) return;
                if (rate != _playInRate)
                {
                    _playInRate = rate;
                    _playResampler = rate == SampleRate ? null : new LinearResampler(rate, SampleRate);
                }
                if (_playResampler != null)
                    pcm = _playResampler.Process(pcm);
                if (pcm.Length == 0) return;

                var bytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                _playback.AddSamples(bytes, 0, bytes.Length);

                if (!_playStarted && _playback.BufferedDuration >= TimeSpan.FromMilliseconds(80))
                {
                    _playStarted = true;
                    try { _speaker?.Play(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[audio] playback decode failed: {ex.Message}");
        }
    }

    private void OnMicStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log?.Invoke($"[audio] mic stopped: {e.Exception.Message}");
        lock (_sync)
        {
            if (_micRunning && e.Exception != null)
            {
                StopMicInternal();
                Log?.Invoke("[audio] mic lost — call continues receive-only");
            }
        }
    }

    public static string[] GetMicDevices()
    {
        var list = new string[WaveIn.DeviceCount];
        for (int i = 0; i < list.Length; i++) list[i] = WaveIn.GetCapabilities(i).ProductName;
        return list;
    }

    public static string[] GetSpeakerDevices()
    {
        var list = new string[WaveOut.DeviceCount];
        for (int i = 0; i < list.Length; i++) list[i] = WaveOut.GetCapabilities(i).ProductName;
        return list;
    }

    private static int FindWaveInDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (int i = 0; i < WaveIn.DeviceCount; i++)
            if (WaveIn.GetCapabilities(i).ProductName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int FindWaveOutDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (int i = 0; i < WaveOut.DeviceCount; i++)
            if (WaveOut.GetCapabilities(i).ProductName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>Stateful linear-interpolation resampler for continuous 16-bit mono streams (block-safe).</summary>
public sealed class LinearResampler
{
    private readonly double _step;
    private double _frac;
    private short _tail;
    private bool _hasTail;

    public LinearResampler(int inRate, int outRate)
    {
        if (inRate <= 0 || outRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        _step = (double)inRate / outRate;
    }

    public short[] Process(short[] input)
    {
        if (input.Length == 0) return Array.Empty<short>();
        int offset = _hasTail ? 1 : 0;
        int n = input.Length + offset;
        var output = new List<short>((int)(input.Length / _step) + 2);

        double pos = _frac;
        while (pos + 1 < n)
        {
            int i0 = (int)pos;
            double t = pos - i0;
            short s0 = At(input, offset, i0);
            short s1 = At(input, offset, i0 + 1);
            output.Add((short)(s0 + (s1 - s0) * t));
            pos += _step;
        }

        _frac = pos - input.Length;
        if (_frac < 0) _frac = 0;
        _tail = input[^1];
        _hasTail = true;
        return output.ToArray();
    }

    private short At(short[] input, int offset, int i)
    {
        if (offset == 0) return input[Math.Min(i, input.Length - 1)];
        if (i == 0) return _tail;
        return input[Math.Min(i - 1, input.Length - 1)];
    }
}
