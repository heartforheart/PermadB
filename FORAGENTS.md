# FORAGENTS.md

## 1. OVERVIEW
- **Project Name**: PermadB
- **Absolute Path**: `A:\Projects2\PermadB`
- **Core Description**: Kernel/driver-level hearing protection and acoustic peak limiter for Windows 10/11 (x64). Intercepts raw 32-bit floating-point PCM audio inside `audiodg.exe` using a custom C++ Windows Audio Processing Object (APO) lookahead brickwall limiter (`PermadBApo.dll`). A companion .NET 9 desktop application continuously calculates acoustically calibrated dBA sound pressure levels, streaming target ceilings and real-time commands to the APO via memory-mapped shared memory (`C:\Users\Public\permadb_apo_telemetry.dat`). The system enforces strict physical hearing safety ceilings while strictly observing the Volume Mixer Immutability Invariant (zero manipulation of Windows Volume Mixer sliders).

---

## 2. TECH STACK
- **Languages**:
  - C++17: Audio Processing Object (`PermadBApo.dll`), DSP math (`PermadBLimiter.h`), and deterministic test suite (`test_dsp.cpp`).
  - C# 13 / .NET 9.0: Main desktop service, WinForms GUI, System Tray manager, WASAPI audio meter polling, HTTP API server, and packaging installers.
  - JavaScript (ES2022) / JSX: React 18 frontend dashboard.
  - PowerShell 5.1+: Driver registration (`install_apo.ps1`, `uninstall_apo.ps1`).
- **Frameworks & SDKs**:
  - Windows Core Audio / APO SDK: `audioenginebaseapo.h`, `audioengineextensionapo.h`, `baseaudioprocessingobject.h`, `AudioBaseProcessingObjectV140.lib`, `audiomediatypecrt.lib`, `audioeng.lib`.
  - Audio Interop: NAudio (`NAudio.Wasapi` v2.2.1) for WASAPI endpoint enumeration, session manager tracking, and audio peak metering.
  - UI Web Framework: React 18.3.1, Vite 8.2.2, Lucide React (`lucide-react` v0.475.0), Recharts (`recharts` v2.15.1).
- **Storage & IPC**:
  - File-Backed Shared Memory Mapped File: `C:\Users\Public\permadb_apo_telemetry.dat` (shared memory name `Global\PermadB_APO_Telemetry`, struct `PermadBApoTelemetry`).
  - Configuration: File-backed JSON at `%APPDATA%\PermadB\config.json`.
  - Windows Registry: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\<DeviceID>\FxProperties` (registered as `PKEY_FX_EndpointEffectClsid` and `PKEY_CompositeFX_EndpointEffectClsid`).
- **External APIs & Network**: None. 100% local, air-gapped, zero cloud dependencies. Embedded HTTP/SSE server binds strictly to `localhost:5000`.
- **Environment Variables**: Standard Windows paths (`%APPDATA%`, `%ProgramFiles%`, `%SystemRoot%`). Visual Studio MSVC 2022 x64 build tools (`vcvars64.bat`).

---

## 3. ARCHITECTURE & DATA FLOW
1. **Audio Render Stream**: Running applications (browsers, media players, voice clients, games) output digital PCM frames into their respective WASAPI sessions.
2. **Audio Mixing & Interception**: The Windows Audio Service (`audiodg.exe`) mixes active sessions for the default render device. Before hitting the hardware endpoint, the mixed 32-bit floating-point PCM buffer passes through `PermadBApo.dll` via `CPermadBLimiterAPO::APOProcess`.
3. **DSP Lookahead Limiting**: Inside `PermadBLimiter.h`, audio passes through a 5 ms circular lookahead buffer. The peak detector calculates required gain reduction with sub-millisecond attack and smooth exponential release across linked stereo channels. Output samples are strictly bounded to the configured linear ceiling with zero clipping or digital overs.
4. **App Daemon Polling Loop**: `PermadB.exe` (`AudioEngine.cs`) polls WASAPI endpoint meters and active session meters every 50–100 ms. It reads hardware master attenuation and estimates physical acoustic pressure (dBA SPL) using device calibration curves and crest factor offsets.
5. **Acoustic Calibration & Shared Memory IPC**:
   $$\text{Target dBFS} = \text{TargetSafeDbSpl} + 11.0\text{ dB} - \text{EstimatedMaxDbSpl}$$
   $$\text{Linear Ceiling} = 10^{\frac{\text{Target dBFS}}{20}}$$
   `AudioEngine.cs` writes `targetCeilingLinear`, `targetCeilingDbfs`, `isEnabled`, and increments `commandSeq` in `permadb_apo_telemetry.dat`. `PermadBApo.dll` detects the sequence change in real time and updates its internal limiter threshold (`SetCeilingLinear`).
6. **Application-Specific Limiting Protocol**:
   - Every application defaults to `Bypassed` (`false`) with zero hardcoded presets.
   - When App-Specific Mode is enabled, protected applications (e.g. Discord) are bounded directly at their individual session level to the active preset safe ceiling (`profile.SafeCeilingPercent / 100.0f`).
   - Bypassed applications (e.g. Brave, Spotify, YouTube) are completely untouched; the Master APO runs in transparent peak-limiter mode (`-1.0 dBFS` / `0.8912` linear), allowing bypassed music to blast at 100% full scale without cross-interference or ducking.
   - In Master Mode (`AppFilteringEnabled == false`), session volumes are untouched and the Master APO enforces global lookahead limiting.
   - Comprehensive diagnostic logging is written to `C:\Users\Public\permadb_debug.log`.
7. **Telemetry & Client Presentation**:
   - `PermadBApo.dll` writes live input peak, output peak, gain reduction (dB), and activation counts to telemetry shared memory.
   - `AudioEngine.cs` broadcasts metrics via events to the native WinForms Dashboard (`DashboardForm.cs`), System Tray (`TrayIconManager.cs`), and SSE web stream (`ApiServer.cs` at `/api/meter`).

---

## 4. KEY FILES & DIRECTORIES

### Root
- [`build.bat`](file:///A:/Projects2/PermadB/build.bat): Master build script. Sequentially compiles C++ APO, builds Vite React assets, compiles .NET 9 app, compiles uninstaller, and packages `PermadB-Setup.exe`.
- [`PermadB-Setup.exe`](file:///A:/Projects2/PermadB/PermadB-Setup.exe): Compiled, self-extracting distribution installer.
- [`driver_package/`](file:///A:/Projects2/PermadB/driver_package/): Staged driver store files (`PermadBApo.dll`, `PermadBApo.inf`, `PermadBApo.cat`).
- [`dist/`](file:///A:/Projects2/PermadB/dist/): Staged compiled binaries ready for payload packaging.

### C++ APO Driver (`PermadBCode/APO/`)
- [`PermadBApo.cpp`](file:///A:/Projects2/PermadB/PermadBCode/APO/PermadBApo.cpp) / [`PermadBApo.h`](file:///A:/Projects2/PermadB/PermadBCode/APO/PermadBApo.h): COM object implementing `IAudioProcessingObject`, `IAudioProcessingObjectConfiguration`, and `IAudioProcessingObjectRT`. Handles `audiodg.exe` buffer hooks and telemetry shared memory mapping.
- [`PermadBLimiter.h`](file:///A:/Projects2/PermadB/PermadBCode/APO/PermadBLimiter.h): Header-only, zero-allocation DSP lookahead brickwall limiter engine with stereo-linked gain calculation.
- [`test_dsp.cpp`](file:///A:/Projects2/PermadB/PermadBCode/APO/test_dsp.cpp): Standalone deterministic DSP unit test suite verifying 6 core limiter invariants.
- [`PermadBApo.inf`](file:///A:/Projects2/PermadB/PermadBCode/APO/PermadBApo.inf) / [`PermadBApo.cdf`](file:///A:/Projects2/PermadB/PermadBCode/APO/PermadBApo.cdf): Driver installation INF configuration and catalog definitions.
- [`build_apo.bat`](file:///A:/Projects2/PermadB/PermadBCode/APO/build_apo.bat): MSVC x64 compilation script for `PermadBApo.dll` and `makecat` catalog generation.
- [`install_apo.ps1`](file:///A:/Projects2/PermadB/PermadBCode/APO/install_apo.ps1) / [`uninstall_apo.ps1`](file:///A:/Projects2/PermadB/PermadBCode/APO/uninstall_apo.ps1): PowerShell scripts registering/unregistering CLSID `{968ff234-1895-49f1-8b97-9d9075eb25b6}` and EFX endpoint keys.

### .NET 9 Application (`PermadBCode/App/`)
- [`Audio/AudioEngine.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Audio/AudioEngine.cs): Core engine orchestrating WASAPI audio endpoints, telemetry shared memory IPC, acoustic decibel calculations, and dynamic app protection logic.
- [`Forms/DashboardForm.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Forms/DashboardForm.cs): Native WinForms desktop GUI with in-window view-swapping between the primary dashboard and App Protection manager.
- [`Tray/TrayIconManager.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Tray/TrayIconManager.cs): Windows System Tray icon, live context menus, preset switching, and 1:1 synchronized per-app protection toggles.
- [`Config/ConfigManager.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Config/ConfigManager.cs) / [`Config/AppSettings.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Config/AppSettings.cs): Thread-safe JSON settings management and device profile storage.
- [`Server/ApiServer.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Server/ApiServer.cs): Embedded lightweight HTTP REST API and Server-Sent Events (SSE) server for UI communication.
- [`Program.cs`](file:///A:/Projects2/PermadB/PermadBCode/App/Program.cs): Application entrypoint, single-instance mutex check, and service initialization.

### Web UI (`PermadBCode/UI/`)
- [`src/App.jsx`](file:///A:/Projects2/PermadB/PermadBCode/UI/src/App.jsx): React dashboard displaying real-time dBA gauges, frequency bars, preset selectors, and per-app limiting toggles.
- [`vite.config.js`](file:///A:/Projects2/PermadB/PermadBCode/UI/vite.config.js): Vite bundler configured to export production assets directly into `PermadBCode/App/wwwroot/`.

### Packaging & Setup (`installer/`)
- [`InstallerEngine.cs`](file:///A:/Projects2/PermadB/installer/InstallerEngine.cs) / [`InstallerForm.cs`](file:///A:/Projects2/PermadB/installer/InstallerForm.cs): Self-extracting setup wizard that deploys payload, configures startup, registers APO driver, and restarts `audiodg.exe`.
- [`uninstaller/`](file:///A:/Projects2/PermadB/installer/uninstaller/): Standalone uninstaller removing registry keys, APO references, and application files.

---

## 5. FEATURES & CAPABILITIES
- **Zero-Overshoot Brickwall Limiting**: 5 ms lookahead delay buffer prevents sudden transient bursts, gunshots, screams, and feedback loops from exceeding target dBFS.
- **Acoustic Transducer Calibration**: Real-time estimation of physical sound pressure level (dBA SPL) at the user's ears using transducer sensitivity baselines, crest factors, and hardware master attenuation.
- **Calibrated Acoustic Presets**:
  - `Safe`: 75 dBA target ceiling (-16.0 dBFS linear).
  - `Night`: 68 dBA target ceiling (-23.0 dBFS linear).
  - `Studio`: 82 dBA target ceiling (-9.0 dBFS linear).
  - `Custom 1`: User-configurable lowest bound (down to 10% / 55 dBA).
  - `Custom 2`: User-configurable highest bound (up to 100% / 88 dBA).
- **Application-Specific Selective Limiting**:
  - Independent `[🛡️ LIMITED]` vs `[⚡ BYPASSED]` state per application.
  - User-driven configuration: zero hardcoded presets so background services (e.g. Discord, Steam) never falsely clamp playback.
  - Real-time dynamic switching: clamps output when a limited app is active; unclamps to transparent -1.0 dBFS when only bypassed apps play.
- **Volume Mixer Immutability Invariant**: Strictly zero writes to Windows Volume Mixer sliders; eliminates slider jumping, pumping, and motorized fighting.
- **Synchronized Dual Interface**: Native WinForms desktop GUI + System Tray menu + React web interface with 100% bidirectional state parity.
- **Deterministic DSP Verification Suite**: Automated 6/6 test verification ensuring zero DC offset, stereo balance preservation, in-place lookahead interception, and dynamic ceiling compliance.

---

## 6. COMMANDS

### Prerequisites & Dependencies
```powershell
# Restore Node dependencies for Web UI
cd A:\Projects2\PermadB\PermadBCode\UI
npm install
```

### Full Build (All Components & Setup Installer)
```powershell
cmd /c "A:\Projects2\PermadB\build.bat"
```

### Component Builds
```powershell
# 1. Compile C++ APO Limiter DLL & Generate Catalog (Requires MSVC x64)
cmd /c "A:\Projects2\PermadB\PermadBCode\APO\build_apo.bat"

# 2. Build Vite React UI
cd A:\Projects2\PermadB\PermadBCode\UI
npm run build

# 3. Build .NET 9 Desktop Application (Debug)
cd A:\Projects2\PermadB\PermadBCode\App
dotnet build PermadB.csproj

# 4. Publish .NET 9 Desktop Application (Release)
cd A:\Projects2\PermadB\PermadBCode\App
dotnet publish PermadB.csproj -c Release -r win-x64 --self-contained false -o A:\Projects2\PermadB\dist
```

### Testing & Verification
```powershell
# 1. Run C++ Deterministic DSP Unit Test Suite (6 tests)
cmd /c "call ""A:\Windows Software\Visual Studio Code\VS3\VC\Auxiliary\Build\vcvars64.bat"" && cd /d A:\Projects2\PermadB\PermadBCode\APO && test_dsp.exe"

# 2. Pre-flight Syntax & Compile Verification
agy-guard PermadBCode/App/Audio/AudioEngine.cs

# 3. Git Scope & Diff Audit
agy-diff-stat
```

### Running & Deploying
```powershell
# Run desktop daemon in development mode
dotnet run --project A:\Projects2\PermadB\PermadBCode\App\PermadB.csproj

# Launch setup wizard / installer
A:\Projects2\PermadB\PermadB-Setup.exe

# Silent install
A:\Projects2\PermadB\PermadB-Setup.exe /S
```
