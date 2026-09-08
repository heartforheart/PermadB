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
    public uint Magic;                  // "PERM" (0x5045524D)
    public uint Version;                // 2 (Phase 2 Lookahead Limiter)
    public uint AudiodgPid;             // PID of audiodg.exe
    public uint SampleRate;             // e.g. 48000
    public uint ChannelCount;           // e.g. 2
    public uint LimiterActivations;     // Total limiter activation count
    public ulong ProcessCount;          // Total APOProcess() invocations
    public ulong TotalFramesProcessed;  // Total frames processed
    public float ConfiguredCeilingDbfs; // -1.0 dBFS
    public float ConfiguredCeilingLinear;// 0.8912509f
    public float LastPeakInLinear;      // Peak linear input [0..1+]
    public float LastPeakOutLinear;     // Peak linear output [0..1+]
    public float LastPeakInDbfs;        // Peak dBFS in
    public float LastPeakOutDbfs;       // Peak dBFS out
    public float MaxObservedInDbfs;     // Highest input dBFS observed
    public float MaxObservedOutDbfs;    // Highest output dBFS observed
    public float CurrentGainReductionDb;// Current gain reduction in dB
    public float MaxGainReductionDb;    // Maximum gain reduction in dB
    public ulong LastProcessTick;       // GetTickCount64()
    public float TargetCeilingLinear;   // Control to APO: target linear ceiling (e.g. 0.30f for 30%)
    public float TargetCeilingDbfs;     // Control to APO: target dBFS ceiling
    public uint IsEnabled;              // Control to APO: 1 = active, 0 = bypass
    public uint CommandSeq;             // Incremented when command changed
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

    private static (MemoryMappedFile?, MemoryMappedViewAccessor?) TryOpenTelemetry()
    {
        string telemFilePath = @"C:\Users\Public\permadb_apo_telemetry.dat";
        if (File.Exists(telemFilePath))
        {
            try
            {
                var fs = new FileStream(telemFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                var mmf = MemoryMappedFile.CreateFromFile(fs, null, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                var acc = mmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite);
                return (mmf, acc);
            }
            catch { }
        }

        try
        {
            var mmf = MemoryMappedFile.OpenExisting(ShmemName, MemoryMappedFileRights.Read);
            var acc = mmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.Read);
            return (mmf, acc);
        }
        catch { }

        return (null, null);
    }

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
        // Step 2b: Test Instantiation via CoCreateInstance
        // -----------------------------------------------------------
        Console.Write("[CHECK 2b] COM Object Instantiation via CoCreateInstance: ");
        try
        {
            Type? apoType = Type.GetTypeFromCLSID(new Guid(ClsidStr));
            if (apoType != null)
            {
                object? instance = Activator.CreateInstance(apoType);
                if (instance != null)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"PASS (Created {instance.GetType().FullName})");
                    Console.ResetColor();
                    Marshal.ReleaseComObject(instance);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL (Activator returned null)");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL (GetTypeFromCLSID returned null)");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL ({ex.GetType().Name}: {ex.Message})");
            Console.ResetColor();
        }
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
        bool hasEfxModes = false;
        using (var fxKey = Registry.LocalMachine.OpenSubKey(fxRegPath))
        {
            if (fxKey != null)
            {
                var efx = fxKey.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7")?.ToString();
                var compositeRaw = fxKey.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15");
                string? compositeEfx = compositeRaw switch
                {
                    string s => s,
                    string[] arr => arr.Length > 0 ? arr[0] : null,
                    _ => null
                };
                associatedClsid = efx ?? compositeEfx;

                var efxModesRaw = fxKey.GetValue("{d3993a3f-99c2-4402-b5ec-a92a0367664b},7");
                hasEfxModes = efxModesRaw != null;
            }
        }

        if (string.Equals(associatedClsid, ClsidStr, StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASS ({defaultDevice.FriendlyName}, ModeSupport: {(hasEfxModes ? "YES" : "NO")})");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNING (Current: '{associatedClsid ?? "none"}', Target: '{ClsidStr}')");
            Console.ResetColor();
            Console.WriteLine("          The APO is not associated with this default device yet.");
            allPassed = false;
        }

        // -----------------------------------------------------------
        // Step 4: Check audiodg.exe and Shared Memory Telemetry
        // -----------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("[CHECK 4] Monitoring audiodg.exe APO Telemetry...");
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        (mmf, accessor) = TryOpenTelemetry();
        if (accessor != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("          Successfully connected to APO Telemetry!");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("          Telemetry not yet active (waiting for audiodg.exe to render audio).");
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
                    (mmf, accessor) = TryOpenTelemetry();
                }

                if (accessor != null)
                {
                    accessor.Read(0, out PermadBApoTelemetry t);
                    Console.WriteLine($"          [t={i * 200}ms] In: {t.LastPeakInDbfs:F1} dBFS | Out: {t.LastPeakOutDbfs:F1} dBFS | MaxOut: {t.MaxObservedOutDbfs:F1} dBFS | MaxGR: {t.MaxGainReductionDb:F1} dB | Act: {t.LimiterActivations} | Vol: {volumeDuring * 100.0f:F1}%");
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
        Console.WriteLine("                 PHASE 2 VERIFICATION RESULTS");
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

            Console.Write("3. Brickwall Ceiling Enforcement:   ");
            // Ceiling is -1.0 dBFS. Output peak must not exceed ceiling (+ 0.05 dB epsilon)
            bool ceilingRespected = finalTelem.MaxObservedOutDbfs <= (finalTelem.ConfiguredCeilingDbfs + 0.05f);
            bool limiterEngaged = finalTelem.LimiterActivations > 0;

            if (ceilingRespected && limiterEngaged)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS (Peak: {finalTelem.MaxObservedOutDbfs:F2} dBFS <= Ceiling {finalTelem.ConfiguredCeilingDbfs:F1} dBFS, Max GR: {finalTelem.MaxGainReductionDb:F2} dB)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL (Peak: {finalTelem.MaxObservedOutDbfs:F2} dBFS, Ceiling: {finalTelem.ConfiguredCeilingDbfs:F1} dBFS, Activations: {finalTelem.LimiterActivations})");
                Console.ResetColor();
                allPassed = false;
            }

            Console.WriteLine();
            Console.WriteLine("--- Telemetry Snapshot Details ---");
            Console.WriteLine($"audiodg PID:            {finalTelem.AudiodgPid}");
            Console.WriteLine($"Sample Rate:            {finalTelem.SampleRate} Hz");
            Console.WriteLine($"Channel Count:          {finalTelem.ChannelCount}");
            Console.WriteLine($"Configured Ceiling:     {finalTelem.ConfiguredCeilingDbfs:F2} dBFS ({finalTelem.ConfiguredCeilingLinear:F4} linear)");
            Console.WriteLine($"Input Peak:             {finalTelem.LastPeakInDbfs:F2} dBFS ({finalTelem.LastPeakInLinear:F4} linear)");
            Console.WriteLine($"Output Peak:            {finalTelem.LastPeakOutDbfs:F2} dBFS ({finalTelem.LastPeakOutLinear:F4} linear)");
            Console.WriteLine($"Maximum Observed Out:   {finalTelem.MaxObservedOutDbfs:F2} dBFS");
            Console.WriteLine($"Current Gain Reduction: {finalTelem.CurrentGainReductionDb:F2} dB");
            Console.WriteLine($"Maximum Gain Reduction: {finalTelem.MaxGainReductionDb:F2} dB");
            Console.WriteLine($"Limiter Activations:    {finalTelem.LimiterActivations}");
            Console.WriteLine($"APOProcess() Calls:     {finalTelem.ProcessCount}");
            Console.WriteLine($"Total Frames Processed: {finalTelem.TotalFramesProcessed}");
            Console.WriteLine($"Last Process Tick:      {finalTelem.LastProcessTick}");
            Console.WriteLine("----------------------------------");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("2. APOProcess() Active Execution:    PENDING INSTALLATION / ACTIVATION");
            Console.WriteLine("3. Brickwall Ceiling Enforcement:   PENDING INSTALLATION / ACTIVATION");
            Console.ResetColor();
            allPassed = false;
        }

        Console.WriteLine("==========================================================");
        Console.WriteLine($"STATUS: {(allPassed ? "ALL CHECKS PASSED - PHASE 2 VERIFIED" : "VERIFICATION INCOMPLETE")}");
        Console.WriteLine("==========================================================");
        Console.WriteLine();

        string apoLogPath = @"C:\Users\Public\permadb_apo.log";
        if (File.Exists(apoLogPath))
        {
            Console.WriteLine("[APO LOG - C:\\Users\\Public\\permadb_apo.log]");
            try
            {
                var lines = File.ReadAllLines(apoLogPath);
                int count = Math.Min(lines.Length, 20);
                for (int i = lines.Length - count; i < lines.Length; i++)
                {
                    Console.WriteLine("  " + lines[i]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Could not read log: " + ex.Message);
            }
            Console.WriteLine();
        }
    }
}
