using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ghostcam.Media.Audio;

/// <summary>
/// Low-latency WinMM audio player for 24000 Hz, 16-bit, Mono PCM streams with live VU metering.
/// Uses an 8-slot unmanaged buffer ring to avoid GC pressure during real-time streaming.
/// </summary>
public sealed unsafe class WaveOutAudioPlayer : IDisposable
{
    private const int SampleRate = 24000;
    private const ushort BitsPerSample = 16;
    private const ushort Channels = 1;
    private const int RingSize = 8;
    private const int InitialBufferCapacity = 16384;

    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint CALLBACK_NULL = 0;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;

    private readonly object _syncLock = new();
    private readonly AudioBufferSlot[] _slots = new AudioBufferSlot[RingSize];
    private nint _hWaveOut;
    private int _nextSlot;
    private bool _disposed;
    private volatile bool _monitorAudio;
    private float _currentVuLevel;
    private long _lastChunkTimestamp;

    public WaveOutAudioPlayer()
    {
        for (int i = 0; i < RingSize; i++)
        {
            nint hdrPtr = Marshal.AllocHGlobal(sizeof(WAVEHDR));
            NativeMemory.Clear((void*)hdrPtr, (nuint)sizeof(WAVEHDR));
            nint dataPtr = Marshal.AllocHGlobal(InitialBufferCapacity);

            _slots[i] = new AudioBufferSlot
            {
                Header = (WAVEHDR*)hdrPtr,
                Data = dataPtr,
                Capacity = InitialBufferCapacity,
                InFlight = false
            };
        }
    }

    /// <summary>
    /// Enables or disables speaker playback. Defaults to false to prevent speaker feedback loops.
    /// </summary>
    public bool MonitorAudio
    {
        get => _monitorAudio;
        set
        {
            if (_monitorAudio == value)
            {
                return;
            }

            _monitorAudio = value;
            if (!value)
            {
                lock (_syncLock)
                {
                    ResetPlaybackLocked();
                }
            }
        }
    }

    /// <summary>
    /// Live smoothed RMS/peak VU level (0..100), updated even when MonitorAudio is false, decaying gently if idle.
    /// </summary>
    public float CurrentVuLevel
    {
        get
        {
            long lastTick = Volatile.Read(ref _lastChunkTimestamp);
            float level = Volatile.Read(ref _currentVuLevel);
            if (lastTick == 0 || level <= 0.05f)
            {
                return level <= 0.05f ? 0.0f : level;
            }

            long elapsedMs = (Stopwatch.GetTimestamp() - lastTick) * 1000 / Stopwatch.Frequency;
            if (elapsedMs > 120)
            {
                float steps = (elapsedMs - 100) / 50.0f;
                float decayed = level * MathF.Pow(0.82f, steps);
                return decayed < 0.2f ? 0.0f : decayed;
            }

            return level;
        }
        private set => Volatile.Write(ref _currentVuLevel, value);
    }

    /// <summary>
    /// Computes live VU level from incoming 24 kHz 16-bit Mono PCM bytes and queues playback if MonitorAudio is enabled.
    /// </summary>
    public void PushPcmChunk(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (_disposed)
        {
            return;
        }

        UpdateVuMeter(pcm16Bytes);

        if (!_monitorAudio || pcm16Bytes.Length < 2)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_disposed || !_monitorAudio)
            {
                return;
            }

            if (!EnsureDeviceOpenLocked())
            {
                return;
            }

            int slotIndex = AcquireFreeSlotLocked();
            if (slotIndex < 0)
            {
                // All 8 buffers are currently playing; drop chunk rather than blocking capture thread.
                return;
            }

            ref AudioBufferSlot slot = ref _slots[slotIndex];
            if (pcm16Bytes.Length > slot.Capacity)
            {
                Marshal.FreeHGlobal(slot.Data);
                slot.Data = Marshal.AllocHGlobal(pcm16Bytes.Length);
                slot.Capacity = pcm16Bytes.Length;
            }

            fixed (byte* pSrc = pcm16Bytes)
            {
                Buffer.MemoryCopy(pSrc, (void*)slot.Data, slot.Capacity, pcm16Bytes.Length);
            }

            WAVEHDR* pHdr = slot.Header;
            NativeMemory.Clear(pHdr, (nuint)sizeof(WAVEHDR));
            pHdr->lpData = slot.Data;
            pHdr->dwBufferLength = (uint)pcm16Bytes.Length;

            uint prepareRes = waveOutPrepareHeader(_hWaveOut, pHdr, (uint)sizeof(WAVEHDR));
            if (prepareRes != 0)
            {
                return;
            }

            uint writeRes = waveOutWrite(_hWaveOut, pHdr, (uint)sizeof(WAVEHDR));
            if (writeRes == 0)
            {
                slot.InFlight = true;
                _nextSlot = (slotIndex + 1) % RingSize;
            }
            else
            {
                waveOutUnprepareHeader(_hWaveOut, pHdr, (uint)sizeof(WAVEHDR));
            }
        }
    }

    private void UpdateVuMeter(ReadOnlySpan<byte> pcm16Bytes)
    {
        Volatile.Write(ref _lastChunkTimestamp, Stopwatch.GetTimestamp());

        int sampleCount = pcm16Bytes.Length / 2;
        if (sampleCount <= 0)
        {
            float decayed = CurrentVuLevel * 0.85f;
            CurrentVuLevel = decayed < 0.2f ? 0.0f : decayed;
            return;
        }

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm16Bytes.Slice(0, sampleCount * 2));
        double sumSq = 0.0;
        int peak = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            int s = samples[i];
            int abs = s == short.MinValue ? 32768 : Math.Abs(s);
            if (abs > peak)
            {
                peak = abs;
            }

            double norm = abs / 32768.0;
            sumSq += norm * norm;
        }

        double rms = Math.Sqrt(sumSq / samples.Length);
        double peakNorm = peak / 32768.0;
        float combined = (float)((rms * 0.65) + (peakNorm * 0.35));
        float targetLevel = peak == 0 ? 0.0f : Math.Clamp(MathF.Pow(combined, 0.65f) * 100.0f, 0.0f, 100.0f);

        float prev = Volatile.Read(ref _currentVuLevel);
        float smoothed = targetLevel >= prev
            ? prev + ((targetLevel - prev) * 0.45f)
            : (prev * 0.78f) + (targetLevel * 0.22f);

        CurrentVuLevel = smoothed < 0.2f ? 0.0f : smoothed;
    }

    private bool EnsureDeviceOpenLocked()
    {
        if (_hWaveOut != 0)
        {
            return true;
        }

        var format = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
            nAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
            cbSize = 0
        };

        uint res = waveOutOpen(out nint hWave, WAVE_MAPPER, ref format, 0, 0, CALLBACK_NULL);
        if (res != 0)
        {
            _hWaveOut = 0;
            return false;
        }

        _hWaveOut = hWave;
        return true;
    }

    private int AcquireFreeSlotLocked()
    {
        for (int offset = 0; offset < RingSize; offset++)
        {
            int idx = (_nextSlot + offset) % RingSize;
            ref AudioBufferSlot slot = ref _slots[idx];

            if (!slot.InFlight)
            {
                return idx;
            }

            if ((slot.Header->dwFlags & WHDR_DONE) != 0)
            {
                if ((slot.Header->dwFlags & WHDR_PREPARED) != 0)
                {
                    waveOutUnprepareHeader(_hWaveOut, slot.Header, (uint)sizeof(WAVEHDR));
                }

                slot.InFlight = false;
                return idx;
            }
        }

        return -1;
    }

    private void ResetPlaybackLocked()
    {
        if (_hWaveOut == 0)
        {
            return;
        }

        waveOutReset(_hWaveOut);
        for (int i = 0; i < RingSize; i++)
        {
            ref AudioBufferSlot slot = ref _slots[i];
            if (slot.InFlight || (slot.Header->dwFlags & WHDR_PREPARED) != 0)
            {
                waveOutUnprepareHeader(_hWaveOut, slot.Header, (uint)sizeof(WAVEHDR));
                slot.InFlight = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _monitorAudio = false;

            if (_hWaveOut != 0)
            {
                ResetPlaybackLocked();
                waveOutClose(_hWaveOut);
                _hWaveOut = 0;
            }

            for (int i = 0; i < RingSize; i++)
            {
                ref AudioBufferSlot slot = ref _slots[i];
                if (slot.Data != 0)
                {
                    Marshal.FreeHGlobal(slot.Data);
                    slot.Data = 0;
                }

                if (slot.Header != null)
                {
                    Marshal.FreeHGlobal((nint)slot.Header);
                    slot.Header = null;
                }
            }
        }
    }

    private struct AudioBufferSlot
    {
        public WAVEHDR* Header;
        public nint Data;
        public int Capacity;
        public bool InFlight;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nint reserved;
    }

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutOpen(
        out nint phwo,
        uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        nint dwCallback,
        nint dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutPrepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutWrite(nint hwo, WAVEHDR* pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutUnprepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutReset(nint hwo);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern uint waveOutClose(nint hwo);
}
