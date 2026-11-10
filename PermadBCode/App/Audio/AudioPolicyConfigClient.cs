using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PermadB.Audio;

/// <summary>
/// AudioPolicyConfigClient: Provides direct interop with Windows Internal AudioPolicyConfig WinRT COM factory
/// (IID: ab3d4648-e242-459f-b02f-541c70306324).
/// 
/// Enables per-application audio endpoint routing without modifying Windows Volume Mixer sliders:
/// - Target voice apps (Discord, Audacity) route into virtual sink (VoiceMeeter Input) -> clamped by VirtualStreamBridge.
/// - Bypassed apps (Brave, Spotify, games) route directly to physical endpoint (HyperX) at 100% full volume.
/// - Volume Slider Immutability Invariant preserved at 100%.
/// </summary>
public sealed class AudioPolicyConfigClient : IDisposable
{
    private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string DEVINTERFACE_AUDIO_CAPTURE = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out IntPtr hs);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static extern int WindowsDeleteString(IntPtr hs);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hs, out uint len);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoGetActivationFactory(IntPtr hs, ref Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(
        IntPtr thisPtr,
        uint processId,
        int flow,
        int role,
        IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpointDelegate(
        IntPtr thisPtr,
        uint processId,
        int flow,
        int role,
        out IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllPersistedApplicationDefaultEndpointsDelegate(
        IntPtr thisPtr);

    private readonly object _lock = new();
    private IntPtr _pFactory = IntPtr.Zero;
    private SetPersistedDefaultAudioEndpointDelegate? _setEndpoint;
    private GetPersistedDefaultAudioEndpointDelegate? _getEndpoint;
    private ClearAllPersistedApplicationDefaultEndpointsDelegate? _clearAll;
    private bool _initialized;
    private bool _disposed;

    // Track active application redirects by processId -> deviceId (or null/empty for default)
    private readonly ConcurrentDictionary<uint, string> _redirectedProcesses = new();

    public bool IsAvailable => EnsureInitialized();

    public AudioPolicyConfigClient()
    {
        EnsureInitialized();
    }

    private bool EnsureInitialized()
    {
        if (_initialized && _pFactory != IntPtr.Zero) return true;

        lock (_lock)
        {
            if (_initialized && _pFactory != IntPtr.Zero) return true;

            try
            {
                RoInitialize(1); // RO_INIT_MULTITHREADED
                string className = "Windows.Media.Internal.AudioPolicyConfig";
                IntPtr hClassName = IntPtr.Zero;
                int hrStr = WindowsCreateString(className, className.Length, out hClassName);
                if (hrStr != 0 || hClassName == IntPtr.Zero)
                {
                    AppLogger.Log($"[AudioPolicyConfig] WindowsCreateString failed: 0x{hrStr:X8}");
                    return false;
                }

                Guid iid = new("ab3d4648-e242-459f-b02f-541c70306324");
                IntPtr pFactory = IntPtr.Zero;
                int hrFactory = RoGetActivationFactory(hClassName, ref iid, out pFactory);
                WindowsDeleteString(hClassName);

                if (hrFactory != 0 || pFactory == IntPtr.Zero)
                {
                    AppLogger.Log($"[AudioPolicyConfig] RoGetActivationFactory failed: 0x{hrFactory:X8}");
                    return false;
                }

                _pFactory = pFactory;
                IntPtr vtable = Marshal.ReadIntPtr(_pFactory);

                // Slot 25: SetPersistedDefaultAudioEndpoint
                IntPtr pSlot25 = Marshal.ReadIntPtr(vtable, 25 * IntPtr.Size);
                _setEndpoint = (SetPersistedDefaultAudioEndpointDelegate)Marshal.GetDelegateForFunctionPointer(
                    pSlot25, typeof(SetPersistedDefaultAudioEndpointDelegate));

                // Slot 26: GetPersistedDefaultAudioEndpoint
                IntPtr pSlot26 = Marshal.ReadIntPtr(vtable, 26 * IntPtr.Size);
                _getEndpoint = (GetPersistedDefaultAudioEndpointDelegate)Marshal.GetDelegateForFunctionPointer(
                    pSlot26, typeof(GetPersistedDefaultAudioEndpointDelegate));

                // Slot 27: ClearAllPersistedApplicationDefaultEndpoints
                IntPtr pSlot27 = Marshal.ReadIntPtr(vtable, 27 * IntPtr.Size);
                _clearAll = (ClearAllPersistedApplicationDefaultEndpointsDelegate)Marshal.GetDelegateForFunctionPointer(
                    pSlot27, typeof(ClearAllPersistedApplicationDefaultEndpointsDelegate));

                _initialized = true;
                AppLogger.Log("[AudioPolicyConfig] AudioPolicyConfigClient initialized successfully (Win11 21H2+ vtable bind).");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudioPolicyConfig] Initialization exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Routes an application process ID to a specific audio endpoint device.
    /// If deviceId is null or empty, restores the application to the Windows system default endpoint.
    /// </summary>
    public bool SetApplicationEndpoint(uint processId, string? deviceId)
    {
        if (processId == 0) return false;

        bool wantsDefault = string.IsNullOrWhiteSpace(deviceId);
        if (wantsDefault && !_redirectedProcesses.ContainsKey(processId))
        {
            // Already defaulted, no-op fast path
            return true;
        }
        if (!wantsDefault && _redirectedProcesses.TryGetValue(processId, out var cur) && string.Equals(cur, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            // Already routed to this device, no-op fast path
            return true;
        }

        if (!EnsureInitialized() || _setEndpoint == null || _pFactory == IntPtr.Zero) return false;

        lock (_lock)
        {
            try
            {
                IntPtr hDevStr = IntPtr.Zero;
                if (!wantsDefault)
                {
                    string fullDevId = deviceId!.StartsWith(MMDEVAPI_TOKEN, StringComparison.OrdinalIgnoreCase)
                        ? deviceId!
                        : $"{MMDEVAPI_TOKEN}{deviceId!}{DEVINTERFACE_AUDIO_RENDER}";

                    int hrStr = WindowsCreateString(fullDevId, fullDevId.Length, out hDevStr);
                    if (hrStr != 0)
                    {
                        AppLogger.Log($"[AudioPolicyConfig] WindowsCreateString failed for deviceId '{fullDevId}': 0x{hrStr:X8}");
                        return false;
                    }
                }

                try
                {
                    // Role 0: eConsole, Role 1: eMultimedia
                    int hr0 = _setEndpoint(_pFactory, processId, 0, 0, hDevStr);
                    int hr1 = _setEndpoint(_pFactory, processId, 0, 1, hDevStr);

                    if (hr0 == 0 && hr1 == 0)
                    {
                        if (!wantsDefault)
                        {
                            _redirectedProcesses[processId] = deviceId!;
                            AppLogger.Log($"[AudioPolicyConfig] Successfully routed PID {processId} -> '{deviceId}'");
                        }
                        else
                        {
                            _redirectedProcesses.TryRemove(processId, out _);
                            AppLogger.Log($"[AudioPolicyConfig] Successfully restored PID {processId} -> Default Audio Endpoint");
                        }
                        return true;
                    }
                    else
                    {
                        // 0x80070057 (E_INVALIDARG) occurs when the process does not have an active WASAPI session yet in audiosrv
                        if ((uint)hr0 == 0x80070057 || (uint)hr1 == 0x80070057)
                        {
                            // Expected for non-active audio PIDs; will auto-bind once stream opens
                            return false;
                        }
                        AppLogger.Log($"[AudioPolicyConfig] SetPersisted returned: hr0=0x{hr0:X8}, hr1=0x{hr1:X8} for PID {processId}");
                        return false;
                    }
                }
                finally
                {
                    if (hDevStr != IntPtr.Zero)
                    {
                        WindowsDeleteString(hDevStr);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudioPolicyConfig] SetApplicationEndpoint error for PID {processId}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Gets the persisted default audio endpoint for an application process ID.
    /// Returns the raw device ID, or null if defaulted.
    /// </summary>
    public string? GetApplicationEndpoint(uint processId)
    {
        if (processId == 0) return null;
        if (!EnsureInitialized() || _getEndpoint == null || _pFactory == IntPtr.Zero) return null;

        lock (_lock)
        {
            try
            {
                IntPtr hDevStr = IntPtr.Zero;
                int hr = _getEndpoint(_pFactory, processId, 0, 1, out hDevStr); // eMultimedia
                if (hr == 0 && hDevStr != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr pBuf = WindowsGetStringRawBuffer(hDevStr, out uint len);
                        if (pBuf != IntPtr.Zero && len > 0)
                        {
                            string raw = Marshal.PtrToStringUni(pBuf, (int)len) ?? string.Empty;
                            return UnpackDeviceId(raw);
                        }
                    }
                    finally
                    {
                        WindowsDeleteString(hDevStr);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudioPolicyConfig] GetApplicationEndpoint error for PID {processId}: {ex.Message}");
            }
        }
        return null;
    }

    private static string UnpackDeviceId(string deviceId)
    {
        if (deviceId.StartsWith(MMDEVAPI_TOKEN, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId.Substring(MMDEVAPI_TOKEN.Length);
        }
        if (deviceId.EndsWith(DEVINTERFACE_AUDIO_RENDER, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId.Substring(0, deviceId.Length - DEVINTERFACE_AUDIO_RENDER.Length);
        }
        if (deviceId.EndsWith(DEVINTERFACE_AUDIO_CAPTURE, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId.Substring(0, deviceId.Length - DEVINTERFACE_AUDIO_CAPTURE.Length);
        }
        return deviceId;
    }

    /// <summary>
    /// Clears all persisted application default endpoints, restoring all apps to Windows defaults.
    /// </summary>
    public void ClearAllApplicationEndpoints()
    {
        if (!EnsureInitialized() || _clearAll == null || _pFactory == IntPtr.Zero) return;

        lock (_lock)
        {
            try
            {
                int hr = _clearAll(_pFactory);
                _redirectedProcesses.Clear();
                AppLogger.Log($"[AudioPolicyConfig] ClearAllPersistedApplicationDefaultEndpoints returned: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudioPolicyConfig] ClearAllApplicationEndpoints error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Restores all tracked redirected applications back to system defaults.
    /// </summary>
    public void ResetAllRedirectedApps()
    {
        foreach (var pid in _redirectedProcesses.Keys)
        {
            SetApplicationEndpoint(pid, null);
        }
        _redirectedProcesses.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            ResetAllRedirectedApps();
            if (_pFactory != IntPtr.Zero)
            {
                Marshal.Release(_pFactory);
                _pFactory = IntPtr.Zero;
            }
        }
        catch { }
    }
}
