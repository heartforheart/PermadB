using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PermadB.Audio;

/// <summary>
/// VirtualStreamBridge: Routes audio from a virtual audio sink (e.g. VoiceMeeter Input)
/// through PermadB's 5ms lookahead brickwall safety limiter, and injects the limited audio
/// directly into the active physical playback endpoint (e.g. HyperX Cloud III Wireless).
/// 
/// This achieves 100% stream separation:
/// - Target voice apps (Discord) play into the virtual sink -> clamped strictly to safe ceiling (e.g. 75 dBA).
/// - Untargeted music/games (Brave, Spotify) play directly into the physical endpoint at 100% unconstrained blast.
/// - Windows Volume Mixer sliders are NEVER touched (Volume Immutability Invariant preserved).
/// </summary>
public sealed class VirtualStreamBridge : IDisposable
{
    private readonly object _syncLock = new();
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _renderer;
    private BufferedWaveProvider? _waveBuffer;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    private float _linearCeiling = 0.35f; // Initial safe ceiling (~75 dBA)
    private float _ceilingDbfs = -9.1f;
    private string _targetDeviceId = string.Empty;

    // Limiter DSP state
    private float[] _delayBuffer = Array.Empty<float>();
    private int _delayIndex;
    private int _lookaheadFrames;
    private int _channels = 2;
    private int _sampleRate = 48000;

    // Monotonic deque for O(1) running window peak detection
    private struct PeakItem
    {
        public ulong FrameIndex;
        public float Peak;
    }
    private PeakItem[] _peakDeque = Array.Empty<PeakItem>();
    private int _dequeHead;
    private int _dequeTail;
    private int _dequeCount;
    private int _dequeCapacity;
    private ulong _frameCounter;

    // Gain smoothing state
    private float _currentGain = 1.0f;
    private float _attackCoeff;
    private float _releaseCoeff;
    private int _holdFramesTotal;
    private int _holdFramesRemaining;

    // Telemetry & metrics
    private float _lastPeakInLinear;
    private float _lastPeakOutLinear;
    private float _currentGainReductionDb;
    private ulong _limiterActivations;
    private DateTime _lastLogTime = DateTime.MinValue;

    public bool IsActive => _isRunning;
    public float LinearCeiling => _linearCeiling;
    public float CurrentGainReductionDb => _currentGainReductionDb;
    public ulong LimiterActivations => _limiterActivations;

    public VirtualStreamBridge()
    {
        EnsureVirtualDeviceActive();
    }

    /// <summary>
    /// Checks HKLM MMDevices registry to ensure VoiceMeeter VAIO is enabled (DeviceState = 1).
    /// </summary>
    public static void EnsureVirtualDeviceActive()
    {
        try
        {
            const string vmGuid = "{e2b7e35a-a3a1-4f82-b5b0-48e65a3f594c}";
            string regPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{vmGuid}";
            using var key = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
            if (key != null)
            {
                var val = key.GetValue("DeviceState");
                if (val is int intVal && intVal != 1)
                {
                    key.SetValue("DeviceState", 1, RegistryValueKind.DWord);
                    AppLogger.Log($"[VirtualStreamBridge] VoiceMeeter Input DeviceState restored from {intVal} to 1 (Active).");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[VirtualStreamBridge] Note: EnsureVirtualDeviceActive check: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the active VoiceMeeter Input MMDevice.
    /// </summary>
    public static MMDevice? FindVirtualSinkDevice(MMDeviceEnumerator enumerator)
    {
        EnsureVirtualDeviceActive();
        try
        {
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    string name = dev.FriendlyName;
                    if (name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) ||
                        dev.ID.Contains("e2b7e35a", StringComparison.OrdinalIgnoreCase))
                    {
                        return dev;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[VirtualStreamBridge] Error finding virtual sink: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Starts the bridge: captures from the virtual sink, runs lookahead DSP limiter,
    /// and renders to the specified physical output device.
    /// </summary>
    public void Start(MMDeviceEnumerator enumerator, MMDevice physicalOutputDevice, float initialLinearCeiling)
    {
        lock (_syncLock)
        {
            if (_isRunning) Stop();

            _targetDeviceId = physicalOutputDevice.ID;
            UpdateCeiling(initialLinearCeiling);

            var virtualSink = FindVirtualSinkDevice(enumerator);
            if (virtualSink == null)
            {
                AppLogger.Log("[VirtualStreamBridge] Warning: VoiceMeeter Input virtual sink device not found or inactive.");
                return;
            }

            try
            {
                _capture = new WasapiLoopbackCapture(virtualSink);
                var waveFormat = _capture.WaveFormat;
                InitDsp(waveFormat.SampleRate, waveFormat.Channels);

                _waveBuffer = new BufferedWaveProvider(waveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(200),
                    DiscardOnBufferOverflow = true
                };

                _renderer = new WasapiOut(physicalOutputDevice, AudioClientShareMode.Shared, false, 20);
                _renderer.Init(_waveBuffer);
                _renderer.Play();

                _capture.DataAvailable += OnAudioDataAvailable;
                _capture.StartRecording();

                _isRunning = true;
                AppLogger.Log($"[VirtualStreamBridge] Bridge started: Sink='{virtualSink.FriendlyName}' -> Output='{physicalOutputDevice.FriendlyName}', Rate={waveFormat.SampleRate}Hz, Ch={waveFormat.Channels}, Ceiling={_linearCeiling:F4} ({_ceilingDbfs:F1} dBFS)");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[VirtualStreamBridge] Failed to start bridge: {ex.Message}");
                Stop();
            }
        }
    }

    /// <summary>
    /// Updates the limiter ceiling in real-time without interrupting the stream.
    /// </summary>
    public void UpdateCeiling(float linearCeiling)
    {
        _linearCeiling = Math.Clamp(linearCeiling, 0.01f, 0.8912509f);
        _ceilingDbfs = 20.0f * (float)Math.Log10(_linearCeiling);
    }

    /// <summary>
    /// Initializes DSP filter buffers and coefficients.
    /// </summary>
    private void InitDsp(int sampleRate, int channels)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _channels = channels > 0 ? channels : 2;

        const float lookaheadMs = 5.0f;
        const float holdMs = 120.0f;
        const float releaseMs = 250.0f;

        _lookaheadFrames = (int)Math.Round(_sampleRate * (lookaheadMs / 1000.0f));
        if (_lookaheadFrames < 1) _lookaheadFrames = 1;

        int totalDelaySamples = _lookaheadFrames * _channels;
        _delayBuffer = new float[totalDelaySamples];
        _delayIndex = 0;

        _dequeCapacity = _lookaheadFrames + 16;
        _peakDeque = new PeakItem[_dequeCapacity];
        _dequeHead = 0;
        _dequeTail = 0;
        _dequeCount = 0;
        _frameCounter = 0;

        _holdFramesTotal = (int)Math.Round(_sampleRate * (holdMs / 1000.0f));
        _holdFramesRemaining = 0;

        _releaseCoeff = (float)Math.Exp(-1.0 / (_sampleRate * (releaseMs / 1000.0)));
        float attackSec = (lookaheadMs * 0.65f) / 1000.0f;
        if (attackSec < 0.0005f) attackSec = 0.0005f;
        _attackCoeff = (float)Math.Exp(-1.0 / (_sampleRate * attackSec));

        _currentGain = 1.0f;
    }

    /// <summary>
    /// Event handler for captured PCM float data from virtual sink.
    /// Real-time safe: processes in-place and queues into renderer buffer.
    /// </summary>
    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRunning || _renderer == null || _waveBuffer == null || e.BytesRecorded == 0) return;

        int bytesRecorded = e.BytesRecorded;
        byte[] rawBuffer = e.Buffer;

        int sampleCount = bytesRecorded / 4;
        if (sampleCount == 0 || _channels == 0) return;
        int frameCount = sampleCount / _channels;

        // Convert byte buffer to float samples
        float[] floatSamples = new float[sampleCount];
        Buffer.BlockCopy(rawBuffer, 0, floatSamples, 0, bytesRecorded);

        // Process through 5ms Lookahead Brickwall Limiter
        ProcessDspLimiter(floatSamples, frameCount);

        // Convert limited float samples back to byte buffer
        Buffer.BlockCopy(floatSamples, 0, rawBuffer, 0, bytesRecorded);

        // Feed limited audio to physical headphones
        _waveBuffer.AddSamples(rawBuffer, 0, bytesRecorded);

        // Diagnostic logging every 3 seconds if audio is passing through
        var now = DateTime.UtcNow;
        if ((now - _lastLogTime).TotalSeconds >= 3.0 && _lastPeakInLinear > 0.001f)
        {
            _lastLogTime = now;
            float inDbfs = _lastPeakInLinear > 0.00001f ? 20.0f * (float)Math.Log10(_lastPeakInLinear) : -96.0f;
            float outDbfs = _lastPeakOutLinear > 0.00001f ? 20.0f * (float)Math.Log10(_lastPeakOutLinear) : -96.0f;
            AppLogger.Log($"[VIRTUAL_STREAM_BRIDGE] In={inDbfs:F1}dBFS, Out={outDbfs:F1}dBFS, GR={_currentGainReductionDb:F1}dB, Clamped={_limiterActivations}, Ceiling={_ceilingDbfs:F1}dBFS");
        }
    }

    /// <summary>
    /// Mathematically identical 5ms lookahead limiter matching PermadBLimiter.h.
    /// </summary>
    private void ProcessDspLimiter(float[] samples, int frameCount)
    {
        int channels = _channels;
        float ceilingLinear = _linearCeiling;

        for (int f = 0; f < frameCount; f++)
        {
            int frameOffset = f * channels;

            // 1. Linked peak detection across channels
            float inLinkedPeak = 0.0f;
            for (int c = 0; c < channels; c++)
            {
                float absVal = Math.Abs(samples[frameOffset + c]);
                if (absVal > inLinkedPeak) inLinkedPeak = absVal;
            }

            if (inLinkedPeak > _lastPeakInLinear) _lastPeakInLinear = inLinkedPeak;

            // 2. Push to monotonic deque for running window maximum
            ulong currentFrame = _frameCounter++;

            while (_dequeCount > 0)
            {
                int backIdx = (_dequeTail == 0) ? (_dequeCapacity - 1) : (_dequeTail - 1);
                if (_peakDeque[backIdx].Peak <= inLinkedPeak)
                {
                    _dequeTail = backIdx;
                    _dequeCount--;
                }
                else
                {
                    break;
                }
            }

            _peakDeque[_dequeTail].FrameIndex = currentFrame;
            _peakDeque[_dequeTail].Peak = inLinkedPeak;
            _dequeTail = (_dequeTail + 1) % _dequeCapacity;
            _dequeCount++;

            // Pop expired frames
            while (_dequeCount > 0)
            {
                if (_peakDeque[_dequeHead].FrameIndex + (ulong)_lookaheadFrames <= currentFrame)
                {
                    _dequeHead = (_dequeHead + 1) % _dequeCapacity;
                    _dequeCount--;
                }
                else
                {
                    break;
                }
            }

            float lookaheadPeak = (_dequeCount > 0) ? _peakDeque[_dequeHead].Peak : inLinkedPeak;

            // 3. Compute target gain
            float targetGain = 1.0f;
            if (lookaheadPeak > ceilingLinear)
            {
                targetGain = ceilingLinear / lookaheadPeak;
                _limiterActivations++;
            }

            // 4. Smooth gain envelope (Attack downwards, Hold, Release upwards)
            if (targetGain < _currentGain)
            {
                _currentGain = _currentGain * _attackCoeff + targetGain * (1.0f - _attackCoeff);
                if (_currentGain < targetGain) _currentGain = targetGain;
                _holdFramesRemaining = _holdFramesTotal;
            }
            else
            {
                if (_holdFramesRemaining > 0)
                {
                    _holdFramesRemaining--;
                }
                else
                {
                    _currentGain = _currentGain * _releaseCoeff + 1.0f * (1.0f - _releaseCoeff);
                    if (_currentGain > 1.0f) _currentGain = 1.0f;
                }
            }

            _currentGainReductionDb = _currentGain < 0.9999f ? 20.0f * (float)Math.Log10(_currentGain) : 0.0f;

            // 5. Apply gain to delayed sample from circular delay buffer
            int delayOffset = _delayIndex * channels;
            float outPeak = 0.0f;

            for (int c = 0; c < channels; c++)
            {
                float delayedSample = _delayBuffer[delayOffset + c];
                _delayBuffer[delayOffset + c] = samples[frameOffset + c]; // Store current incoming sample

                float limitedSample = delayedSample * _currentGain;
                // Brickwall clamp
                if (limitedSample > ceilingLinear) limitedSample = ceilingLinear;
                else if (limitedSample < -ceilingLinear) limitedSample = -ceilingLinear;

                samples[frameOffset + c] = limitedSample;

                float absOut = Math.Abs(limitedSample);
                if (absOut > outPeak) outPeak = absOut;
            }

            _delayIndex = (_delayIndex + 1) % _lookaheadFrames;
            _lastPeakOutLinear = outPeak;
        }
    }

    /// <summary>
    /// Stops the bridge and disposes audio streams cleanly.
    /// </summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            try
            {
                _capture?.StopRecording();
                _capture?.Dispose();
                _capture = null;
            }
            catch { }

            try
            {
                _renderer?.Stop();
                _renderer?.Dispose();
                _renderer = null;
            }
            catch { }

            _waveBuffer = null;
            AppLogger.Log("[VirtualStreamBridge] Bridge stopped cleanly.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
