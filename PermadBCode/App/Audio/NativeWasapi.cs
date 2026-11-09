using System;
using System.Runtime.InteropServices;

namespace PermadB.Audio;

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl([MarshalAs(UnmanagedType.LPStruct)] Guid AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
    [PreserveSig] int GetSimpleAudioVolume([MarshalAs(UnmanagedType.LPStruct)] Guid AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    [PreserveSig] int RegisterSessionNotification(IntPtr NewSessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr NewSessionNotification);
    [PreserveSig] int RegisterDuckNotification(string sessionID, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int SessionCount);
    [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl Session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int State);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] int GetGroupingParam(out Guid pRetVal);
    [PreserveSig] int SetGroupingParam([MarshalAs(UnmanagedType.LPStruct)] Guid Override, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2 : IAudioSessionControl
{
    [PreserveSig] new int GetState(out int State);
    [PreserveSig] new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] new int GetGroupingParam(out Guid pRetVal);
    [PreserveSig] new int SetGroupingParam([MarshalAs(UnmanagedType.LPStruct)] Guid Override, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] new int RegisterAudioSessionNotification(IntPtr NewNotifications);
    [PreserveSig] new int UnregisterAudioSessionNotification(IntPtr NewNotifications);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetProcessId(out uint pRetVal);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float pfPeak);
    [PreserveSig] int GetMeteringChannelCount(out int pnChannelCount);
    [PreserveSig] int GetChannelsPeakValues(int u32ChannelCount, [In] IntPtr afPeakValues);
    [PreserveSig] int QueryHardwareSupport(out int pdwHardwareSupportMask);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float fLevel, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] int GetMasterVolume(out float pfLevel);
    [PreserveSig] int SetMute(bool bMute, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig] int GetMute(out bool pbMute);
}
