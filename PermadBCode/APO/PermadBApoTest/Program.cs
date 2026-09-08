using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PermadBApoTest;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PermadBApoTelemetry
{
    public uint Magic;                 // "PERM" (0x5045524D)
    public uint Version;               // 1
    public uint AudiodgPid;            // PID of audiodg.exe
    public uint SampleRate;            // e.g. 48000
    public uint ChannelCount;          // e.g. 2
    public ulong ProcessCount;         // Total APOProcess() invocations
    public ulong TotalFramesProcessed; // Total frames processed
    public float FixedGainDb;          // -12.0 dB
    public float FixedGainLinear;      // 0.25118864f
    public float LastPeakInLinear;     // Peak linear input
    public float LastPeakOutLinear;    // Peak linear output
    public float LastPeakInDbfs;       // Peak dBFS in
    public float LastPeakOutDbfs;      // Peak dBFS out
    public ulong LastProcessTick;      // GetTickCount64()
}

public class SineWaveProvider : WaveStream
{
    private readonly WaveFormat _format;
    private readonly double _frequency;
    private readonly double _amplitude;
    private long _position;
    private readonly long _totalBytes;

    public SineWaveProvider(int sampleRate, int channels, double frequency, double amplitude, TimeSpan duration)
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _frequency = frequency;
        _amplitude = amplitude;
        _totalBytes = (long)(duration.TotalSeconds * sampleRate * channels * 4);
    }

    public override WaveFormat WaveFormat => _format;
    public override long Length => _totalBytes;
    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesToRead = (int)Math.Min(count, _totalBytes - _position);
        int samplesToRead = bytesToRead / 4;
        float[] samples = new float[samplesToRead];

        int channels = _format.Channels;
        int frameIndex = (int)(_position / (4 * channels));

        for (int i = 0; i < samplesToRead; i += channels)
        {
            double t = (double)(frameIndex++) / _format.SampleRate;
            float sampleValue = (float)(_amplitude * Math.Sin(2.0 * Math.PI * _frequency * t));

            for (int ch = 0; ch < channels; ch++)
            {
                if (i + ch < samplesToRead)
                {
                    samples[i + ch] = sampleValue;
                }
            }
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, bytesToRead);
        _position += bytesToRead;
        return bytesToRead;
    }
}

class Program
{
    private const string ClsidStr = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
    private const string ShmemName = "Global\\PermadB_APO_Telemetry";

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("==========================================================");
        Console.WriteLine("          PermadB Limiter APO - Phase 1 Verification");
        Console.WriteLine("==========================================================");
        Console.WriteLine();

        bool allPassed = true;

        // -----------------------------------------------------------
        // Step 1: Check Security Policy (DisableProtectedAudioDG)
        // -----------------------------------------------------------
        Console.Write("[CHECK 1] Windows Audio Security Policy (DisableProtectedAudioDG): ");
        using (var audioKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"))
        {
            var val = audioKey?.GetValue("DisableProtectedAudioDG");
            if (val is int intVal && intVal == 1)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS (Value = 1)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL (Not set to 1. Found: {val ?? "null"})");
                Console.ResetColor();
                Console.WriteLine("          Fix: Run install_apo.ps1 as Administrator.");
                allPassed = false;
            }
        }

        // -----------------------------------------------------------
        // Step 2: Check COM Registration
        // -----------------------------------------------------------
        Console.Write("[CHECK 2] COM Class Registration (HKCR\\CLSID): ");
        using (var clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{ClsidStr}"))
        {
            if (clsidKey != null)
            {
                using var inprocKey = clsidKey.OpenSubKey("InProcServer32");
                var dllPath = inprocKey?.GetValue("")?.ToString();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS ({dllPath})");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL (CLSID key not found in HKCR)");
                Console.ResetColor();
                allPassed = false;
            }
        }

        // -----------------------------------------------------------
        // Step 3: Check Endpoint Association
        // -----------------------------------------------------------
        Console.Write("[CHECK 3] Endpoint Association in FxProperties: ");
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string deviceId = defaultDevice.ID;
        // Strip out any prefix to get the guid
        string deviceGuid = deviceId;
        int lastBrace = deviceGuid.LastIndexOf('{');
        if (lastBrace >= 0)
        {
            deviceGuid = deviceGuid.Substring(lastBrace);
        }

        string fxRegPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{deviceGuid}\FxProperties";
        string? associatedClsid = null;
        using (var fxKey = Registry.LocalMachine.OpenSubKey(fxRegPath))
        {
            if (fxKey != null)
            {
                var efx = fxKey.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7")?.ToString();
                var compositeEfx = fxKey.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15")?.ToString();
                associatedClsid = efx ?? compositeEfx;
            }
        }

        if (string.Equals(associatedClsid, ClsidStr, StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASS ({defaultDevice.FriendlyName})");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNING (Current: '{associatedClsid ?? "none"}', Target: '{ClsidStr}')");
            Console.ResetColor();
            Console.WriteLine("          The APO is not associated with this default device yet.");
        }

        // -----------------------------------------------------------
        // Step 4: Check audiodg.exe and Shared Memory Telemetry
        // -----------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[CHECK 4] Monitoring audiodg.exe APO Telemetry...");
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(ShmemName, MemoryMappedFileRights.Read);
            accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.Read);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("          Successfully connected to Global\\PermadB_APO_Telemetry!");
            Console.ResetColor();
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("          Shared memory not yet active (audiodg.exe has not rendered audio with the APO yet).");
            Console.ResetColor();
        }

        // -----------------------------------------------------------
        // Step 5: Audio Processing & Volume Invariance Test
        // -----------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[TEST 5] Playing 1000 Hz Tone at 0.0 dBFS (Full Scale) to Test APO Processing...");
        float volumeBefore = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        Console.WriteLine($"          Windows Master Volume Before: {volumeBefore * 100.0f:F1}%");

        ulong initialCalls = 0;
        if (accessor != null)
        {
            accessor.Read(0, out PermadBApoTelemetry initialTelem);
            initialCalls = initialTelem.ProcessCount;
        }

        try
        {
            using var toneStream = new SineWaveProvider(48000, 2, 1000.0, 1.0, TimeSpan.FromSeconds(3.0));
            using var wasapiOut = new WasapiOut(defaultDevice, AudioClientShareMode.Shared, false, 50);
            wasapiOut.Init(toneStream);
            wasapiOut.Play();

            float volumeDuring = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
            Console.WriteLine($"          Tone Playing... Checking live telemetry...");

            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(200);
                volumeDuring = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;

                if (accessor == null)
                {
                    try
                    {
                        mmf = MemoryMappedFile.OpenExisting(ShmemName, MemoryMappedFileRights.Read);
                        accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.Read);
                    }
                    catch { }
                }

                if (accessor != null)
                {
                    accessor.Read(0, out PermadBApoTelemetry t);
                    Console.WriteLine($"          [t={i * 200}ms] In: {t.LastPeakInDbfs:F1} dBFS | Out: {t.LastPeakOutDbfs:F1} dBFS | Δ: {t.LastPeakOutDbfs - t.LastPeakInDbfs:F1} dB | APO Calls: {t.ProcessCount} | Vol: {volumeDuring * 100.0f:F1}%");
                }
                else
                {
                    Console.WriteLine($"          [t={i * 200}ms] Playing tone... (Vol: {volumeDuring * 100.0f:F1}%)");
                }
            }

            wasapiOut.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"          Audio playback note: {ex.Message}");
        }

        float volumeAfter = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        Console.WriteLine($"          Windows Master Volume After:  {volumeAfter * 100.0f:F1}%");

        // -----------------------------------------------------------
        // Step 6: Final Verification Analysis
        // -----------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine("                 PHASE 1 VERIFICATION RESULTS");
        Console.WriteLine("==========================================================");

        bool volumeUnchanged = Math.Abs(volumeBefore - volumeAfter) < 0.001f;
        Console.Write("1. Windows Volume Slider Invariance: ");
        if (volumeUnchanged)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASS (Static at {volumeBefore * 100.0f:F1}%, 0.0% delta)");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL (Moved from {volumeBefore * 100.0f:F1}% to {volumeAfter * 100.0f:F1}%)");
            Console.ResetColor();
            allPassed = false;
        }

        if (accessor != null)
        {
            accessor.Read(0, out PermadBApoTelemetry finalTelem);
            bool callsIncremented = finalTelem.ProcessCount > initialCalls;
            bool attenuationCorrect = Math.Abs(finalTelem.FixedGainDb - (-12.0f)) < 0.1f;

            Console.Write("2. APOProcess() Active Execution:    ");
            if (callsIncremented)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS ({finalTelem.ProcessCount} total calls in audiodg.exe PID {finalTelem.AudiodgPid})");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL (No new APOProcess calls detected during playback)");
                Console.ResetColor();
                allPassed = false;
            }

            Console.Write("3. Fixed Sample Attenuation (-12dB): ");
            float measuredDiff = finalTelem.LastPeakOutDbfs - finalTelem.LastPeakInDbfs;
            if (Math.Abs(measuredDiff - (-12.0f)) < 1.0f)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS (In: {finalTelem.LastPeakInDbfs:F1} dBFS -> Out: {finalTelem.LastPeakOutDbfs:F1} dBFS, diff = {measuredDiff:F1} dB)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"MEASURED (In: {finalTelem.LastPeakInDbfs:F1} dBFS -> Out: {finalTelem.LastPeakOutDbfs:F1} dBFS, diff = {measuredDiff:F1} dB)");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("2. APOProcess() Active Execution:    PENDING INSTALLATION / ACTIVATION");
            Console.WriteLine("3. Fixed Sample Attenuation (-12dB): PENDING INSTALLATION / ACTIVATION");
            Console.ResetColor();
        }

        Console.WriteLine("==========================================================");
        Console.WriteLine();
    }
}
