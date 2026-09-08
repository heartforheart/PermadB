# 📦 PermadB Limiter APO — Driver Package (Engineering Draft)

This directory contains the draft Windows Audio Processing Object (APO) driver package for **PermadB Limiter APO**, structured according to Windows 11 in-box APO reference models.

---

## 📂 Package Contents

| Filename | Description | Status |
| :--- | :--- | :--- |
| `PermadBApo.inf` | AudioProcessingObject setup information file (`Class=AudioProcessingObject`, `ClassGuid={5989fce8-9cd0-467d-8a6a-5419e31529d4}`). | **Draft** (Modeled on Windows 11 in-box APOs; pending formal WDK `InfVerif.exe /u` validation). |
| `PermadBApo.cat` | Windows security catalog file containing SHA-256 digital hashes of `PermadBApo.inf` and `PermadBApo.dll`. | **Generated** via Windows SDK `makecat.exe`. |
| `PermadBApo.dll` | Native C++17 lookahead brickwall peak limiter 64-bit Audio Processing Object binary. | **Compiled & Verified** in development mode (`DisableProtectedAudioDG=1`). |

---

## 🛠️ Local Staging & Testing (via pnputil)

To test staging this package into the local Windows DriverStore on a test machine (Administrator prompt):

```cmd
pnputil /add-driver PermadBApo.inf /install
```

To inspect installed OEM drivers and find the assigned `oemXX.inf`:

```cmd
pnputil /enum-drivers | findstr /i "PermadBApo"
```

To remove the package cleanly from the DriverStore:

```cmd
pnputil /delete-driver oemXX.inf /uninstall /force
```

---

## 🔑 Phase B — Microsoft Hardware Dev Center & Signing Reality

To distribute this APO so that Windows 11 `audiodg.exe` loads it natively without `DisableProtectedAudioDG=1`:

1. **The Role of `PETrust` ([Microsoft SignatureAttributes Documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-signatureattributes-section))**:
   * `PermadBApo.inf` includes `SignatureAttributes.PETrust = true`.
   * **Important Reality**: Microsoft documentation explicitly clarifies that simply adding `PETrust = true` to an INF **does not grant the signature**. Microsoft only issues this signature attribute if the package passes the required **Hardware Lab Kit (HLK)** test playlist.
   * Standard automated **Attestation Signing** (which skips HLK tests) does **not** grant `PETrust` and will not permit unsigned APO execution under Protected Process Light (PPL).

2. **Requirements for Official Production Signing**:
   * Microsoft Partner Center Hardware Dev Center account.
   * Extended Validation (EV) Code Signing Certificate.
   * Execution and submission of the official **Windows Hardware Compatibility Program (WHCP) Audio Lab Kit (HLK)** test suite.
   * Validation of the INF using the Windows Driver Kit `InfVerif.exe /u` tool.

Community contributions from Windows audio driver engineers with HLK lab environments are warmly welcomed!
