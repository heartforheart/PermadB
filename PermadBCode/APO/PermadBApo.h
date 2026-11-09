// PermadBApo.h - PermadB Master Limiter Audio Processing Object (APO)
#pragma once

#include <windows.h>
#include <unknwn.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <baseaudioprocessingobject.h>
#include <cmath>

#include "PermadBLimiter.h"

// CLSID for EFX (Endpoint / Post-Mix Master Limiter): {968ff234-1895-49f1-8b97-9d9075eb25b6}
static const GUID CLSID_PermadBLimiterAPO =
    { 0x968ff234, 0x1895, 0x49f1, { 0x8b, 0x97, 0x9d, 0x90, 0x75, 0xeb, 0x25, 0xb6 } };
static const GUID CLSID_PermadBEfxApo = CLSID_PermadBLimiterAPO;

// CLSID for SFX (Stream / Pre-Mix Per-Stream Limiter): {b41e8c71-2947-4e3a-965a-06385d8204d1}
static const GUID CLSID_PermadBSfxApo =
    { 0xb41e8c71, 0x2947, 0x4e3a, { 0x96, 0x5a, 0x06, 0x38, 0x5d, 0x82, 0x04, 0xd1 } };

// Effect Type ID: {72da89f2-2b62-4f38-9cf1-ec9e4f208c90}
static const GUID PermadBEffectId =
    { 0x72da89f2, 0x2b62, 0x4f38, { 0x9c, 0xf1, 0xec, 0x9e, 0x4f, 0x20, 0x8c, 0x90 } };

#define PERMADB_TELEMETRY_MAGIC 0x5045524D // "PERM"
#define PERMADB_SHMEM_NAME L"Global\\PermadB_APO_Telemetry"
#define PERMADB_SFX_MAX_SLOTS 16

enum PermadBAudioMode : UINT32
{
    PermadBMode_Default = 0,
    PermadBMode_Communications = 1,
    PermadBMode_Media = 2,
    PermadBMode_Other = 3
};

#pragma pack(push, 8)
struct PermadBSfxSlot
{
    volatile UINT32 active;               // 0 = free, 1 = claimed by active SFX instance
    UINT32 instanceId;                    // Monotonic token incremented on each claim
    UINT32 audioProcessingMode;           // 0=Default, 1=Communications, 2=Media, 3=Other
    FLOAT32 inputPeak;                    // Max pre-limiter linear peak observed [0..1+]
    FLOAT32 outputPeak;                   // Max post-limiter linear peak observed [0..1+]
    FLOAT32 targetCeilingLinear;          // Target linear ceiling (default 0.1585f / -16dBFS)
    UINT32 bypass;                        // 0 = limit to targetCeilingLinear, 1 = bypass SFX
    UINT32 matchedProcessId;              // Matched by C# supervisor (0 = unmatched)
};

struct PermadBApoTelemetry
{
    UINT32 magic;                         // "PERM" (0x5045524D)
    UINT32 version;                       // 3 (Dual-Stage SFX + EFX)
    UINT32 audiodgPid;                    // PID of audiodg.exe
    UINT32 sampleRate;                    // e.g. 48000
    UINT32 channelCount;                  // e.g. 2
    UINT32 limiterActivations;            // Total limiter activation count
    UINT64 processCount;                  // Total APOProcess() invocations
    UINT64 totalFramesProcessed;          // Total frames processed
    FLOAT32 configuredCeilingDbfs;        // -1.0 dBFS
    FLOAT32 configuredCeilingLinear;       // 0.8912509f
    FLOAT32 lastPeakInLinear;             // Peak linear input [0..1+]
    FLOAT32 lastPeakOutLinear;            // Peak linear output [0..1+]
    FLOAT32 lastPeakInDbfs;               // Peak dBFS in
    FLOAT32 lastPeakOutDbfs;              // Peak dBFS out
    FLOAT32 maxObservedInDbfs;            // Highest input dBFS observed
    FLOAT32 maxObservedOutDbfs;           // Highest output dBFS observed
    FLOAT32 currentGainReductionDb;       // Current gain reduction in dB
    FLOAT32 maxGainReductionDb;           // Maximum gain reduction in dB
    UINT64 lastProcessTick;               // GetTickCount64()
    FLOAT32 targetCeilingLinear;          // Control from App: target linear ceiling (e.g. 0.30f for 30%)
    FLOAT32 targetCeilingDbfs;            // Control from App: target dBFS ceiling
    UINT32 isEnabled;                     // Control from App: 1 = limiter active, 0 = bypass
    UINT32 commandSeq;                    // Incremented by App whenever command changes
    FLOAT32 masterSafetyCeilingLinear;    // Master safety cap for EFX brickwall (e.g. 0.8912f or safe ceiling)
    UINT32 sfxActiveCount;                // Number of active SFX instances
    PermadBSfxSlot slots[PERMADB_SFX_MAX_SLOTS]; // Fixed table of 16 SFX stream slots
};
#pragma pack(pop)

static_assert(sizeof(PermadBSfxSlot) == 32, "PermadBSfxSlot must be exactly 32 bytes");
static_assert(sizeof(PermadBApoTelemetry) == 624, "PermadBApoTelemetry must be exactly 624 bytes");

class CPermadBLimiterAPO;

class CNonDelegatingUnknown : public IUnknown
{
private:
    CPermadBLimiterAPO* m_pOwner;
    volatile LONG m_cRef;
public:
    CNonDelegatingUnknown(CPermadBLimiterAPO* pOwner);
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;
};

class CPermadBLimiterAPO :
    public CBaseAudioProcessingObject,
    public IAudioSystemEffects2
{
    friend class CNonDelegatingUnknown;
private:
    CNonDelegatingUnknown m_nonDelegatingUnknown;
    IUnknown* m_pUnkOuter;
    PermadBApoTelemetry* m_pTelemetry;
    HANDLE m_hFile;
    HANDLE m_hMapFile;
    HANDLE m_hEffectsChangedEvent;
    CPermadBLimiter m_limiter;
    UINT32 m_lastCommandSeq;

    void InitTelemetry();
    void CleanupTelemetry();
    void UpdateTelemetry(UINT32 u32Frames, UINT32 u32Channels, FLOAT32 lastInLinear, FLOAT32 lastOutLinear);

public:
    CPermadBLimiterAPO(IUnknown* pUnkOuter = nullptr);
    virtual ~CPermadBLimiterAPO();

    IUnknown* GetNonDelegatingUnknown() { return &m_nonDelegatingUnknown; }

    // IUnknown (Delegating)
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IAudioProcessingObject
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;
    STDMETHOD(Reset)() override;

    // IAudioProcessingObjectConfiguration
    STDMETHOD(LockForProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)() override;

    // IAudioProcessingObjectRT
    STDMETHOD_(void, APOProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override;

    // IAudioSystemEffects2
    STDMETHOD(GetEffectsList)(
        _Outptr_result_buffer_maybenull_(*pcEffects) LPGUID* ppEffectsIds,
        _Out_ UINT* pcEffects,
        _In_ HANDLE Event) override;
};

// ==========================================================================
// CPermadBSfxApo - Pre-Mix Per-Stream Limiter APO (SFX)
// ==========================================================================
class CPermadBSfxApo;

class CNonDelegatingUnknownSfx : public IUnknown
{
private:
    CPermadBSfxApo* m_pOwner;
    volatile LONG m_cRef;
public:
    CNonDelegatingUnknownSfx(CPermadBSfxApo* pOwner);
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;
};

class CPermadBSfxApo :
    public CBaseAudioProcessingObject,
    public IAudioSystemEffects2
{
    friend class CNonDelegatingUnknownSfx;
private:
    CNonDelegatingUnknownSfx m_nonDelegatingUnknown;
    IUnknown* m_pUnkOuter;
    PermadBApoTelemetry* m_pTelemetry;
    HANDLE m_hFile;
    HANDLE m_hMapFile;
    HANDLE m_hEffectsChangedEvent;
    CPermadBLimiter m_limiter;
    int m_slotIndex; // -1 if not claimed, 0..15 if claimed
    UINT32 m_instanceId;
    UINT32 m_audioProcessingMode;

    void InitTelemetry();
    void CleanupTelemetry();
    int ClaimSlot(UINT32 mode);
    void ReleaseSlot();

public:
    CPermadBSfxApo(IUnknown* pUnkOuter = nullptr);
    virtual ~CPermadBSfxApo();

    IUnknown* GetNonDelegatingUnknown() { return &m_nonDelegatingUnknown; }

    // IUnknown (Delegating)
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IAudioProcessingObject
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;
    STDMETHOD(Reset)() override;

    // IAudioProcessingObjectConfiguration
    STDMETHOD(LockForProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)() override;

    // IAudioProcessingObjectRT
    STDMETHOD_(void, APOProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override;

    // IAudioSystemEffects2
    STDMETHOD(GetEffectsList)(
        _Outptr_result_buffer_maybenull_(*pcEffects) LPGUID* ppEffectsIds,
        _Out_ UINT* pcEffects,
        _In_ HANDLE Event) override;
};
