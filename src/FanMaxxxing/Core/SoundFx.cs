using System.IO;
using System.Media;

namespace FanMaxxxing.Core;

/// <summary>
/// Efectos de sonido sintetizados en memoria (sin archivos externos). Se generan una vez y se reutilizan.
/// </summary>
public sealed class SoundFx : IDisposable
{
    private const int SampleRate = 22050;
    private readonly Lazy<(MemoryStream, SoundPlayer)> _turbine;
    private readonly Lazy<(MemoryStream, SoundPlayer)> _chime;
    private readonly Lazy<(MemoryStream, SoundPlayer)> _boop;
    private bool _disposed;

    public bool Enabled { get; set; } = true;

    public SoundFx()
    {
        _turbine = new(() => Make(Turbine()));
        _chime = new(() => Make(Chime()));
        _boop = new(() => Make(Boop()));
    }

    public void PlayTurbine() => Play(_turbine);
    public void PlayChime() => Play(_chime);
    public void PlayBoop() => Play(_boop);

    private void Play(Lazy<(MemoryStream, SoundPlayer)> s)
    {
        if (!Enabled || _disposed) return;
        try
        {
            var (ms, player) = s.Value;
            ms.Position = 0;
            player.Play(); // asíncrono, no bloquea la UI
        }
        catch (Exception ex)
        {
            Log.ErrorThrottled("No se pudo reproducir sonido", ex);
        }
    }

    private static (MemoryStream, SoundPlayer) Make(short[] samples)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);  // PCM
            w.Write((short)1);  // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return (ms, player);
    }

    /// <summary>"Despegue": ruido filtrado + tono que sube.</summary>
    private static short[] Turbine()
    {
        double dur = 1.9;
        int n = (int)(SampleRate * dur);
        var data = new short[n];
        var rng = new Random(42);
        double lp = 0, phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;
            double cutoff = 0.02 + 0.25 * t * t;
            double noise = rng.NextDouble() * 2 - 1;
            lp += (noise - lp) * cutoff;
            double freq = 120 + 900 * t * t;
            phase += 2 * Math.PI * freq / SampleRate;
            double tone = Math.Sin(phase) * 0.25 + Math.Sin(phase * 2.01) * 0.1;
            double env = Math.Min(1, t * 6) * (t > 0.85 ? (1 - t) / 0.15 : 1);
            double v = (lp * 1.6 + tone) * env * 0.55;
            data[i] = (short)(Math.Clamp(v, -1, 1) * short.MaxValue);
        }
        return data;
    }

    /// <summary>Campanita de "logro desbloqueado".</summary>
    private static short[] Chime()
    {
        double[] notes = { 659.25, 783.99, 1046.5, 1318.5 }; // Mi, Sol, Do, Mi
        double noteDur = 0.11, tail = 0.6;
        int n = (int)(SampleRate * (noteDur * notes.Length + tail));
        var data = new double[n];
        for (int k = 0; k < notes.Length; k++)
        {
            int start = (int)(SampleRate * noteDur * k);
            int len = n - start;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / SampleRate;
                double env = Math.Exp(-t * 5);
                data[start + i] += (Math.Sin(2 * Math.PI * notes[k] * t) + 0.3 * Math.Sin(4 * Math.PI * notes[k] * t)) * env * 0.22;
            }
        }
        return data.Select(v => (short)(Math.Clamp(v, -1, 1) * short.MaxValue)).ToArray();
    }

    private static short[] Boop()
    {
        int n = (int)(SampleRate * 0.12);
        var data = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double f = 520 - 1800 * t;
            double env = 1 - (double)i / n;
            data[i] = (short)(Math.Sin(2 * Math.PI * f * t) * env * 0.35 * short.MaxValue);
        }
        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var l in new[] { _turbine, _chime, _boop })
        {
            if (!l.IsValueCreated) continue;
            try
            {
                l.Value.Item2.Stop();
                l.Value.Item2.Dispose();
                l.Value.Item1.Dispose();
            }
            catch { /* ignorar */ }
        }
    }
}
