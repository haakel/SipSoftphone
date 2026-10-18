using System.Media;
using NAudio.Wave;

namespace SipSoftphone;

/// <summary>
/// Plays a looping .wav ringtone while an incoming call is ringing.
/// Falls back to the Windows default exclamation sound when no file is set/found.
/// </summary>
public sealed class RingtonePlayer : IDisposable
{
    private readonly object _sync = new();
    private WaveOut? _out;
    private AudioFileReader? _reader;
    private bool _playing;

    public bool IsPlaying { get { lock (_sync) return _playing; } }

    public void Play(string? path)
    {
        Stop();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var reader = new AudioFileReader(path) { Volume = 0.8f };
                var waveOut = new WaveOut { DeviceNumber = -1, Volume = 1f };
                waveOut.Init(reader);
                waveOut.PlaybackStopped += OnPlaybackStopped;
                waveOut.Play();
                lock (_sync)
                {
                    _reader = reader;
                    _out = waveOut;
                    _playing = true;
                }
                return;
            }
            catch
            {
                // fall through to default sound
            }
        }
        SystemSounds.Exclamation.Play();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        WaveOut? restart = null;
        lock (_sync)
        {
            if (_playing && _out != null && sender == _out)
            {
                try { _reader!.Position = 0; restart = _out; } catch { _playing = false; }
            }
        }
        try { restart?.Play(); } catch { lock (_sync) _playing = false; }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _playing = false;
            if (_out != null)
            {
                try { _out.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { _out.Stop(); } catch { }
                try { _out.Dispose(); } catch { }
                _out = null;
            }
            if (_reader != null)
            {
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }
        }
    }

    public void Dispose() => Stop();
}
