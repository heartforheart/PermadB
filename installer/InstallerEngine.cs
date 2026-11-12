using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace PermadB.Installer;

public static class InstallerEngine
{
    public static string GetDefaultInstallDir()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "PermadB");
    }

    public static void Install(string destinationDir, bool desktopShortcut, bool startMenuShortcut, bool startWithWindows, bool launchNow, Action<string, int>? progressCallback = null)
    {
        progressCallback?.Invoke("Stopping existing PermadB processes...", 10);
        KillExistingProcesses();

        progressCallback?.Invoke("Preparing installation directory...", 20);
        if (!Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Reset user config on install / reinstall so it defaults cleanly to Safe Ears
        try
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PermadB");
            var configPath = Path.Combine(appDataDir, "config.json");
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
        catch { }

        progressCallback?.Invoke("Extracting PermadB files...", 30);
        ExtractPayload(destinationDir);

        // Install PermadB Lookahead Brickwall Limiter APO into Windows Audio Engine
        InstallApo(destinationDir, progressCallback);

        progressCallback?.Invoke("Configuring shortcuts...", 75);
        var exePath = Path.Combine(destinationDir, "PermadB.exe");

        if (desktopShortcut)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktopDir, "PermadB.lnk"), exePath, destinationDir);
        }

        if (startMenuShortcut)
        {
            var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(Path.Combine(startMenuDir, "PermadB.lnk"), exePath, destinationDir);
        }

        progressCallback?.Invoke("Configuring system registry...", 85);
        if (startWithWindows)
        {
            SetStartupRegistry(exePath);
        }

        RegisterAddRemovePrograms(destinationDir, exePath);

        progressCallback?.Invoke("Finalizing installation...", 100);

        if (launchNow && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = destinationDir,
                UseShellExecute = true
            });
        }
    }

    private static void InstallApo(string destinationDir, Action<string, int>? progressCallback)
    {
        try
        {
            // Remove legacy or bloated log file if present
            try
            {
                var staleLog = @"C:\Users\Public\permadb_apo.log";
                if (File.Exists(staleLog)) File.Delete(staleLog);
            }
            catch { }

            progressCallback?.Invoke("Configuring Windows Audio Engine security policy...", 40);
            try
            {
                using var audioKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", true);
                audioKey?.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning setting DisableProtectedAudioDG: {ex.Message}");
            }

            progressCallback?.Invoke("Staging APO driver package into Windows DriverStore...", 45);
            try
            {
                var infPath = Path.Combine(destinationDir, "PermadBApo.inf");
                if (!File.Exists(infPath))
                {
                    infPath = Path.Combine(destinationDir, "driver", "PermadBApo.inf");
                }

                if (File.Exists(infPath))
                {
                    var pnpPsi = new ProcessStartInfo("pnputil.exe", $"/add-driver \"{infPath}\" /install")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var pnpProc = Process.Start(pnpPsi);
                    if (pnpProc != null)
                    {
                        var output = pnpProc.StandardOutput.ReadToEnd();
                        pnpProc.WaitForExit(10000);
                        Console.WriteLine($"[InstallApo] pnputil output:\n{output}");

                        var match = Regex.Match(output, @"Published Name\s*:\s*(oem\d+\.inf)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var oemInf = match.Groups[1].Value.Trim();
                            try
                            {
                                using var permadbKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PermadB", true);
                                permadbKey?.SetValue("DriverOemInf", oemInf, RegistryValueKind.String);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning staging driver with pnputil: {ex.Message}");
            }

            progressCallback?.Invoke("Registering PermadB Limiter APO...", 50);
            var apoDllPath = Path.Combine(destinationDir, "PermadBApo.dll");
            if (File.Exists(apoDllPath))
            {
                try
                {
                    var psi = new ProcessStartInfo("regsvr32.exe", $"/s \"{apoDllPath}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[InstallApo] Warning running regsvr32: {ex.Message}");
                }
            }

            // COM & AudioEngine registration for the APO (EFX & SFX)
            try
            {
                var apoEntries = new[]
                {
                    (Clsid: "{968ff234-1895-49f1-8b97-9d9075eb25b6}", Name: "PermadB Master Limiter EFX APO"),
                    (Clsid: "{b41e8c71-2947-4e3a-965a-06385d8204d1}", Name: "PermadB Stream Limiter SFX APO")
                };

                foreach (var entry in apoEntries)
                {
                    using var clsidKey = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\CLSID\{entry.Clsid}", true);
                    if (clsidKey != null)
                    {
                        clsidKey.SetValue("", entry.Name, RegistryValueKind.String);
                        using var inprocKey = clsidKey.CreateSubKey("InProcServer32", true);
                        if (inprocKey != null)
                        {
                            inprocKey.SetValue("", apoDllPath, RegistryValueKind.String);
                            inprocKey.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
                        }
                    }

                    using var apoKey = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{entry.Clsid}", true);
                    if (apoKey != null)
                    {
                        apoKey.SetValue("FriendlyName", entry.Name, RegistryValueKind.String);
                        apoKey.SetValue("Copyright", "PermadB", RegistryValueKind.String);
                        apoKey.SetValue("MajorVersion", 1, RegistryValueKind.DWord);
                        apoKey.SetValue("MinorVersion", 0, RegistryValueKind.DWord);
                        apoKey.SetValue("Flags", 15, RegistryValueKind.DWord);
                        apoKey.SetValue("MinInputConnections", 1, RegistryValueKind.DWord);
                        apoKey.SetValue("MaxInputConnections", 1, RegistryValueKind.DWord);
                        apoKey.SetValue("MinOutputConnections", 1, RegistryValueKind.DWord);
                        apoKey.SetValue("MaxOutputConnections", 1, RegistryValueKind.DWord);
                        apoKey.SetValue("MaxInstances", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                        apoKey.SetValue("NumAPOInterfaces", 2, RegistryValueKind.DWord);
                        apoKey.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}", RegistryValueKind.String);
                        apoKey.SetValue("APOInterface1", "{BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}", RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning registering AudioProcessingObjects key: {ex.Message}");
            }

            progressCallback?.Invoke("Associating APO with audio endpoints...", 60);
            ConfigureEndpointFxProperties();

            progressCallback?.Invoke("Refreshing Windows Audio Service...", 70);
            RestartAudioService();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InstallApo] Unexpected error: {ex.Message}");
        }
    }

    #region Win32 Security & Registry Privileges

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfo(
        string pObjectName,
        int ObjectType,
        int SecurityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    private const int SE_REGISTRY_KEY = 4;
    private const int OWNER_SECURITY_INFORMATION = 1;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege1;
    }

    private static void EnableTokenPrivileges()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                return;

            try
            {
                string[] privs = {
                    "SeTakeOwnershipPrivilege",
                    "SeRestorePrivilege",
                    "SeBackupPrivilege",
                    "SeSecurityPrivilege"
                };

                foreach (var priv in privs)
                {
                    if (LookupPrivilegeValue(null, priv, out LUID luid))
                    {
                        var tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Privilege1 = new LUID_AND_ATTRIBUTES
                            {
                                Luid = luid,
                                Attributes = SE_PRIVILEGE_ENABLED
                            }
                        };
                        AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        catch { }
    }

    private static void TakeOwnershipAndGrantFullControl(string subKeyPath)
    {
        string machinePath = @"MACHINE\" + subKeyPath.TrimStart('\\');

        try
        {
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] adminSidBytes = new byte[adminSid.BinaryLength];
            adminSid.GetBinaryForm(adminSidBytes, 0);

            IntPtr pSid = Marshal.AllocHGlobal(adminSidBytes.Length);
            try
            {
                Marshal.Copy(adminSidBytes, 0, pSid, adminSidBytes.Length);
                SetNamedSecurityInfo(
                    machinePath,
                    SE_REGISTRY_KEY,
                    OWNER_SECURITY_INFORMATION,
                    pSid,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pSid);
            }
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                subKeyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadKey);

            if (key != null)
            {
                var acl = key.GetAccessControl();
                var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

                acl.SetOwner(adminSid);
                acl.AddAccessRule(new RegistryAccessRule(
                    adminSid,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                acl.AddAccessRule(new RegistryAccessRule(
                    usersSid,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                key.SetAccessControl(acl);
            }
        }
        catch { }
    }

    #endregion

    private static void ConfigureEndpointFxProperties()
    {
        EnableTokenPrivileges();

        const string clsidEfx = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
        const string clsidSfx = "{b41e8c71-2947-4e3a-965a-06385d8204d1}";

        const string pkeyLfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
        const string pkeyGfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2";
        const string pkeySfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
        const string pkeyMfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
        const string pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        const string pkeyCompositeSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
        const string pkeyCompositeMfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14";
        const string pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
        const string pkeySfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
        const string pkeyMfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6";
        const string pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";

        var targetModes = new[]
        {
            "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", // DEFAULT
            "{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}", // RAW
            "{98951333-B9CD-48B1-A0A3-FF40682D73F7}", // COMMUNICATIONS
            "{FC1CFC9B-B9D6-4CFA-B5E0-4BB2166878B2}", // SPEECH
            "{4780004E-7133-41D8-8C74-660DADD2C0EE}", // MEDIA
            "{B26FEB0D-EC94-477C-9494-D1AB8E753F6E}", // MOVIE
            "{9CF2A70B-F377-403B-BD6B-360863E0355C}", // NOTIFICATION
            "{FC1CF0A4-3860-4592-8232-A567847BE2D1}", // Speech alias
            "{47800040-4533-461B-9B4C-9E9D77B11057}", // Media alias
            "{B26FEDB9-886C-430B-8018-CB5467E9FC0E}"  // Movie alias
        };

        const string renderBasePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        TakeOwnershipAndGrantFullControl(renderBasePath);

        try
        {
            using var renderKey = Registry.LocalMachine.OpenSubKey(renderBasePath);
            if (renderKey == null) return;

            var subKeyNames = renderKey.GetSubKeyNames();
            foreach (var deviceId in subKeyNames)
            {
                try
                {
                    var devKeyPath = $@"{renderBasePath}\{deviceId}";
                    var fxSubPath = $@"{devKeyPath}\FxProperties";

                    TakeOwnershipAndGrantFullControl(devKeyPath);

                    // Ensure FxProperties subkey exists
                    RegistryKey? fxKey = null;
                    try
                    {
                        fxKey = Registry.LocalMachine.OpenSubKey(fxSubPath, true);
                    }
                    catch { }

                    if (fxKey == null)
                    {
                        try
                        {
                            fxKey = Registry.LocalMachine.CreateSubKey(fxSubPath, true);
                        }
                        catch { }
                    }

                    // Grant ownership & full control on FxProperties subkey itself
                    TakeOwnershipAndGrantFullControl(fxSubPath);

                    if (fxKey == null)
                    {
                        try
                        {
                            fxKey = Registry.LocalMachine.OpenSubKey(fxSubPath, true);
                        }
                        catch { }
                    }

                    if (fxKey != null)
                    {
                        using (fxKey)
                        {
                            const string nullModeGuid = "{00000000-0000-0000-0000-000000000000}";
                            const string pkeyCompositeOsfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},19";
                            const string pkeyCompositeOmfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},20";
                            const string pkeyDisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

                            // 0. Restore legacy DTS SFX multi-string backup if present
                            if (fxKey.GetValue("PermadB_Backup_SFX_DTS") is string[] dtsMulti &&
                                !HasNonEmptyArray(fxKey, "PermadB_Backup_CompositeSFX"))
                            {
                                var cleanedDts = dtsMulti
                                    .Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, clsidSfx, StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                if (cleanedDts.Length > 0)
                                {
                                    fxKey.SetValue(pkeyCompositeSfx, cleanedDts, RegistryValueKind.MultiString);
                                }
                                try { fxKey.DeleteValue("PermadB_Backup_SFX_DTS", false); } catch { }
                            }

                            // 1. Restore OEM SFX/LFX & strip PermadB Per-App SFX so OEM APOs (HyperX DTS, Realtek, etc.) operate normally
                            RestoreCompositeClsid(fxKey, pkeyCompositeSfx, clsidSfx, "PermadB_Created_CompositeSFX", "PermadB_Backup_CompositeSFX_DTS", "PermadB_Backup_CompositeSFX");
                            RestoreStringClsid(fxKey, pkeySfx, clsidSfx, "PermadB_Backup_SFX_DTS", "PermadB_Backup_SFX");
                            RestoreStringClsid(fxKey, pkeyLfx, clsidSfx, "PermadB_Backup_LFX");

                            bool hasOemSfx = HasNonEmptyArray(fxKey, pkeyCompositeSfx) ||
                                             (!string.IsNullOrWhiteSpace(fxKey.GetValue(pkeySfx) as string) &&
                                              !string.Equals(fxKey.GetValue(pkeySfx) as string, clsidSfx, StringComparison.OrdinalIgnoreCase));
                            RestoreOrCleanSfxModes(fxKey, pkeySfxModes, nullModeGuid, hasOemSfx);

                            // 2. Restore OEM MFX only if PermadB left backups/created markers or clsidSfx; never strip native OEM MFX
                            RestoreStringClsid(fxKey, pkeyMfx, clsidSfx, "PermadB_Backup_MFX");
                            RestoreCompositeClsid(fxKey, pkeyCompositeMfx, clsidSfx, "PermadB_Created_CompositeMFX", "PermadB_Backup_CompositeMFX");
                            if (fxKey.GetValue("PermadB_Backup_MFXModes") is string[] backupMfxModes)
                            {
                                var cleanedMfxModes = backupMfxModes
                                    .Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                if (cleanedMfxModes.Length > 0) fxKey.SetValue(pkeyMfxModes, cleanedMfxModes, RegistryValueKind.MultiString);
                                else try { fxKey.DeleteValue(pkeyMfxModes, false); } catch { }
                                try { fxKey.DeleteValue("PermadB_Backup_MFXModes", false); } catch { }
                                try { fxKey.DeleteValue("PermadB_Created_MFXModes", false); } catch { }
                            }
                            else if (fxKey.GetValue("PermadB_Created_MFXModes") != null)
                            {
                                if (!HasNonEmptyArray(fxKey, pkeyCompositeMfx) && string.IsNullOrWhiteSpace(fxKey.GetValue(pkeyMfx) as string))
                                {
                                    try { fxKey.DeleteValue(pkeyMfxModes, false); } catch { }
                                }
                                try { fxKey.DeleteValue("PermadB_Created_MFXModes", false); } catch { }
                            }

                            // 3. Configure Post-Mix Master Limiter EFX without mixing legacy GFX/EFX on CompositeFX endpoints
                            bool usesCompositeFx = HasNonEmptyArray(fxKey, pkeyCompositeSfx) ||
                                                   HasNonEmptyArray(fxKey, pkeyCompositeMfx) ||
                                                   HasNonEmptyArray(fxKey, pkeyCompositeOsfx) ||
                                                   HasNonEmptyArray(fxKey, pkeyCompositeOmfx) ||
                                                   (fxKey.GetValue(pkeyCompositeEfx) is string[] existingCompCheck &&
                                                    existingCompCheck.Any(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, clsidEfx, StringComparison.OrdinalIgnoreCase)));

                            if (usesCompositeFx)
                            {
                                RestoreStringClsid(fxKey, pkeyGfx, clsidEfx, "PermadB_Backup_GFX");
                                RestoreStringClsid(fxKey, pkeyEfx, clsidEfx, "PermadB_Backup_EFX");
                            }
                            else
                            {
                                var existingGfx = fxKey.GetValue(pkeyGfx) as string;
                                if (!string.Equals(existingGfx, clsidEfx, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrWhiteSpace(existingGfx) && fxKey.GetValue("PermadB_Backup_GFX") == null)
                                    {
                                        fxKey.SetValue("PermadB_Backup_GFX", existingGfx, RegistryValueKind.String);
                                    }
                                    fxKey.SetValue(pkeyGfx, clsidEfx, RegistryValueKind.String);
                                }

                                var existingEfx = fxKey.GetValue(pkeyEfx) as string;
                                if (!string.Equals(existingEfx, clsidEfx, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrWhiteSpace(existingEfx) && fxKey.GetValue("PermadB_Backup_EFX") == null)
                                    {
                                        fxKey.SetValue("PermadB_Backup_EFX", existingEfx, RegistryValueKind.String);
                                    }
                                    fxKey.SetValue(pkeyEfx, clsidEfx, RegistryValueKind.String);
                                }
                            }

                            var existingComp = fxKey.GetValue(pkeyCompositeEfx) as string[];
                            if (existingComp == null || existingComp.Length == 0)
                            {
                                if (fxKey.GetValue("PermadB_Backup_CompositeEFX") == null)
                                {
                                    fxKey.SetValue("PermadB_Created_CompositeEFX", 1, RegistryValueKind.DWord);
                                }
                                fxKey.SetValue(pkeyCompositeEfx, new string[] { clsidEfx }, RegistryValueKind.MultiString);
                            }
                            else if (!existingComp.Any(s => string.Equals(s, clsidEfx, StringComparison.OrdinalIgnoreCase)))
                            {
                                if (fxKey.GetValue("PermadB_Backup_CompositeEFX") == null && fxKey.GetValue("PermadB_Created_CompositeEFX") == null)
                                {
                                    fxKey.SetValue("PermadB_Backup_CompositeEFX", existingComp, RegistryValueKind.MultiString);
                                }
                                fxKey.SetValue(pkeyCompositeEfx, existingComp.Append(clsidEfx).ToArray(), RegistryValueKind.MultiString);
                            }

                            var existingEfxModes = fxKey.GetValue(pkeyEfxModes) as string[];
                            if (existingEfxModes == null || existingEfxModes.Length == 0)
                            {
                                if (fxKey.GetValue("PermadB_Backup_EFXModes") == null)
                                {
                                    fxKey.SetValue("PermadB_Created_EFXModes", 1, RegistryValueKind.DWord);
                                }
                                fxKey.SetValue(pkeyEfxModes, targetModes, RegistryValueKind.MultiString);
                            }
                            else if (!targetModes.All(t => existingEfxModes.Any(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase))) ||
                                     existingEfxModes.Any(m => string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase)))
                            {
                                if (fxKey.GetValue("PermadB_Backup_EFXModes") == null && fxKey.GetValue("PermadB_Created_EFXModes") == null)
                                {
                                    fxKey.SetValue("PermadB_Backup_EFXModes", existingEfxModes, RegistryValueKind.MultiString);
                                }
                                var mergedModes = existingEfxModes
                                    .Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase))
                                    .Union(targetModes, StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                fxKey.SetValue(pkeyEfxModes, mergedModes, RegistryValueKind.MultiString);
                            }

                            // 4. Restore Device default effects if Audio Enhancements were toggled Off
                            if (fxKey.GetValue(pkeyDisableSysFx) is int disableSysFx && disableSysFx != 0)
                            {
                                fxKey.SetValue(pkeyDisableSysFx, 0, RegistryValueKind.DWord);
                            }

                            fxKey.SetValue("PermadB_Installed_DeviceId", deviceId, RegistryValueKind.String);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConfigureEndpointFxProperties] Error configuring device {deviceId}: {ex.Message}");
                }
            }

            static bool HasNonEmptyArray(RegistryKey key, string valueName) =>
                key.GetValue(valueName) is string[] arr && arr.Any(s => !string.IsNullOrWhiteSpace(s));

            static void RestoreStringClsid(RegistryKey key, string propKey, string permadbClsid, params string[] backupNames)
            {
                string? restored = null;
                foreach (var bName in backupNames)
                {
                    if (key.GetValue(bName) is string s && !string.IsNullOrWhiteSpace(s) && !string.Equals(s, permadbClsid, StringComparison.OrdinalIgnoreCase))
                    {
                        restored = s;
                        break;
                    }
                }

                if (restored != null)
                {
                    key.SetValue(propKey, restored, RegistryValueKind.String);
                }
                else if (string.Equals(key.GetValue(propKey) as string, permadbClsid, StringComparison.OrdinalIgnoreCase))
                {
                    try { key.DeleteValue(propKey, false); } catch { }
                }

                foreach (var bName in backupNames)
                {
                    try { key.DeleteValue(bName, false); } catch { }
                }
            }

            static void RestoreCompositeClsid(RegistryKey key, string propKey, string permadbClsid, string? createdFlag, params string[] backupNames)
            {
                string[]? restored = null;
                foreach (var bName in backupNames)
                {
                    var raw = key.GetValue(bName);
                    if (raw is string[] arr)
                    {
                        restored = arr.Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, permadbClsid, StringComparison.OrdinalIgnoreCase)).ToArray();
                        break;
                    }
                    if (raw is string s && !string.IsNullOrWhiteSpace(s) && !string.Equals(s, permadbClsid, StringComparison.OrdinalIgnoreCase))
                    {
                        restored = new[] { s };
                        break;
                    }
                }

                if (restored != null && restored.Length > 0)
                {
                    key.SetValue(propKey, restored, RegistryValueKind.MultiString);
                }
                else if (key.GetValue(propKey) is string[] current)
                {
                    var cleaned = current.Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, permadbClsid, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (cleaned.Length != current.Length)
                    {
                        if (cleaned.Length > 0) key.SetValue(propKey, cleaned, RegistryValueKind.MultiString);
                        else try { key.DeleteValue(propKey, false); } catch { }
                    }
                    else if (createdFlag != null && key.GetValue(createdFlag) != null && cleaned.Length == 0)
                    {
                        try { key.DeleteValue(propKey, false); } catch { }
                    }
                }

                foreach (var bName in backupNames)
                {
                    try { key.DeleteValue(bName, false); } catch { }
                }
                if (createdFlag != null)
                {
                    try { key.DeleteValue(createdFlag, false); } catch { }
                }
            }

            static void RestoreOrCleanSfxModes(RegistryKey key, string pkeySfxModes, string nullModeGuid, bool hasOemSfx)
            {
                if (key.GetValue("PermadB_Backup_SFXModes") is string[] backupModes)
                {
                    var cleaned = backupModes.Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (cleaned.Length > 0) key.SetValue(pkeySfxModes, cleaned, RegistryValueKind.MultiString);
                    else try { key.DeleteValue(pkeySfxModes, false); } catch { }
                    try { key.DeleteValue("PermadB_Backup_SFXModes", false); } catch { }
                    try { key.DeleteValue("PermadB_Created_SFXModes", false); } catch { }
                    return;
                }

                bool hadCreatedFlag = key.GetValue("PermadB_Created_SFXModes") != null;
                if (!hasOemSfx && hadCreatedFlag)
                {
                    try { key.DeleteValue(pkeySfxModes, false); } catch { }
                    try { key.DeleteValue("PermadB_Created_SFXModes", false); } catch { }
                    return;
                }

                if (hadCreatedFlag)
                {
                    try { key.DeleteValue("PermadB_Created_SFXModes", false); } catch { }
                }

                if (key.GetValue(pkeySfxModes) is string[] currentModes &&
                    currentModes.Any(m => string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase)))
                {
                    var cleaned = currentModes.Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, nullModeGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (cleaned.Length > 0) key.SetValue(pkeySfxModes, cleaned, RegistryValueKind.MultiString);
                    else try { key.DeleteValue(pkeySfxModes, false); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigureEndpointFxProperties] Error: {ex.Message}");
        }
    }

    private static void RestartAudioService()
    {
        try
        {
            // 1. Terminate all audiodg.exe instances so Windows releases the APO dll
            foreach (var p in Process.GetProcessesByName("audiodg"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }

            // 2. Restart audiosrv service cleanly and synchronously via cmd net commands
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"net stop audiosrv /y & net start AudioEndpointBuilder & net start audiosrv\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            // 3. One more check for any lingering audiodg
            foreach (var p in Process.GetProcessesByName("audiodg"))
            {
                try { p.Kill(); p.WaitForExit(1000); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RestartAudioService] Error: {ex.Message}");
        }
    }

    private static void KillExistingProcesses()
    {
        string[] processNames = { "PermadB", "PermadB-Uninstaller", "PermadB-Installer" };
        int currentPid = Environment.ProcessId;

        foreach (var name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                if (proc.Id == currentPid) continue;
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { }
            }
        }

        foreach (var proc in Process.GetProcessesByName("audiodg"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(2000);
            }
            catch { }
        }

        Thread.Sleep(200);
    }

    private static void ExtractPayload(string destinationDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("PermadB.Installer.payload.zip");
        if (stream == null)
        {
            throw new InvalidOperationException("Embedded payload.zip was not found in installer assembly.");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(Path.Combine(destinationDir, entry.FullName));
                continue;
            }

            var destPath = Path.Combine(destinationDir, entry.FullName);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                entry.ExtractToFile(destPath, overwrite: true);
            }
            catch (Exception)
            {
                try
                {
                    var oldPath = destPath + ".old";
                    if (File.Exists(oldPath)) { try { File.Delete(oldPath); } catch { } }
                    File.Move(destPath, oldPath);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
                catch { }
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = "PermadB - Permanent Audio Decibel Guard";
                shortcut.IconLocation = $"{targetPath},0";
                shortcut.Save();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shortcut] Error creating shortcut {shortcutPath}: {ex.Message}");
        }
    }

    private static void SetStartupRegistry(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("PermadB", $"\"{exePath}\" --minimized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Error setting Run key: {ex.Message}");
        }
    }

    private static void RegisterAddRemovePrograms(string destinationDir, string exePath)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
            if (baseKey == null) return;

            using var subKey = baseKey.CreateSubKey("PermadB");
            if (subKey == null) return;

            var uninstallerPath = Path.Combine(destinationDir, "Uninstall.exe");

            subKey.SetValue("DisplayName", "PermadB - Permanent Audio Decibel Guard");
            subKey.SetValue("DisplayVersion", "1.0.0");
            subKey.SetValue("Publisher", "PermadB");
            subKey.SetValue("InstallLocation", destinationDir);
            subKey.SetValue("UninstallString", $"\"{uninstallerPath}\"");
            subKey.SetValue("DisplayIcon", $"{exePath},0");
            subKey.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            subKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            subKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UninstallRegistry] Error: {ex.Message}");
        }
    }
}
