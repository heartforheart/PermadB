# 📦 PermadB Limiter APO — Production Driver Package

This directory contains the standard, compliant Windows Audio Processing Object (APO) driver package for **PermadB Limiter APO**.

---

## 📂 Package Contents

| Filename | Description |
| :--- | :--- |
| PermadBApo.inf | AudioProcessingObject setup information file (Class=AudioProcessingObject, ClassGuid={5989fce8-9cd0-467d-8a6a-5419e31529d4}). |
| PermadBApo.cat | Windows security catalog file containing SHA-256 digital hashes of PermadBApo.inf and PermadBApo.dll. |
| PermadBApo.dll | Native C++17 lookahead brickwall peak limiter 64-bit Audio Processing Object binary. |

---

## 🛠️ Local Staging & Testing (via pnputil)

To stage this driver package into the Windows DriverStore on a test machine (Administrator prompt):

`cmd
pnputil /add-driver PermadBApo.inf /install
`

To inspect installed OEM drivers and find the assigned oemXX.inf:

`cmd
pnputil /enum-drivers | findstr /i "PermadBApo"
`

To remove the driver package cleanly from the DriverStore:

`cmd
pnputil /delete-driver oemXX.inf /uninstall /force
`

---

## 🔑 Phase B — Microsoft Partner Center / Hardware Dev Center Signing

To achieve full Microsoft Attestation / WHQL signing so that Windows 11 udiodg.exe loads the APO natively under Protected Process Light (PPL) with DisableProtectedAudioDG=0 and Secure Boot enabled:

1. **Prerequisites**:
   * Microsoft Partner Center Hardware Dev Center account.
   * Extended Validation (EV) Code Signing Certificate.
2. **Attestation Submission**:
   * Package PermadBApo.inf, PermadBApo.cat, and PermadBApo.dll into a .cab archive or HLK test package.
   * Sign the .cab using your EV Code Signing Certificate via signtool.exe.
   * Submit to Microsoft Partner Center Hardware Dashboard for Attestation Signing.
3. **Download Signed Package**:
   * Microsoft signs PermadBApo.cat with the official **Microsoft Windows Hardware Compatibility Publisher** signature (PETrust = true).
   * Replace PermadBApo.cat with the Microsoft-signed catalog.
   * The package can now be distributed to all Windows 11 systems worldwide with zero security modifications.
