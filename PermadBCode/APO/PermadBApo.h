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

// CLSID: {968ff234-1895-49f1-8b97-9d9075eb25b6}
static const GUID CLSID_PermadBLimiterAPO =
    { 0x968ff234, 0x1895, 0x49f1, { 0x8b, 0x97, 0x9d, 0x90, 0x75, 0xeb, 0x25, 0xb6 } };

// Effect Type ID: {72da89f2-2b62-4f38-9cf1-ec9e4f208c90}
static const GUID PermadBEffectId =
    { 0x72da89f2, 0x2b62, 0x4f38, { 0x9c, 0xf1, 0xec, 0x9e, 0x4f, 0x20, 0x8c, 0x90 } };

#define PERMADB_TELEMETRY_MAGIC 0x5045524D // "PERM"
#define PERMADB_SHMEM_NAME L"Global\\PermadB_APO_Telemetry"

#pragma pack(push, 8)
struct PermadBApoTelemetry
{
    UINT32 magic;                 // "PERM"
    UINT32 version;               // 1
    UINT32 audiodgPid;            // PID of audiodg.exe
    UINT32 sampleRate;            // e.g. 48000
    UINT32 channelCount;          // e.g. 2
    UINT64 processCount;          // Total APOProcess() invocations
    UINT64 totalFramesProcessed;  // Total frames processed
    FLOAT32 fixedGainDb;          // -12.0 dB
    FLOAT32 fixedGainLinear;      // 0.25118864f
    FLOAT32 lastPeakInLinear;     // Peak linear input [0..1+]
    FLOAT32 lastPeakOutLinear;    // Peak linear output [0..1+]
    FLOAT32 lastPeakInDbfs;       // Peak dBFS in
    FLOAT32 lastPeakOutDbfs;      // Peak dBFS out
    UINT64 lastProcessTick;       // GetTickCount64()
};
#pragma pack(pop)

class CPermadBLimiterAPO :
    public CBaseAudioProcessingObject,
    public IAudioSystemEffects2
{
private:
    volatile LONG m_cRef;
    PermadBApoTelemetry* m_pTelemetry;
    HANDLE m_hMapFile;
    HANDLE m_hEffectsChangedEvent;

    void InitTelemetry();
    void CleanupTelemetry();
    void UpdateTelemetry(UINT32 u32Frames, UINT32 u32Channels, const FLOAT32* pf32In, const FLOAT32* pf32Out);

public:
    CPermadBLimiterAPO();
    virtual ~CPermadBLimiterAPO();

    // IUnknown
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IAudioProcessingObject
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;

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
