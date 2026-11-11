// PermadBApo.cpp - Implementation of PermadB Master Limiter Audio Processing Object
#include "PermadBApo.h"
#include <sddl.h>
#include <algorithm>
#include <cstdio>

static volatile LONG g_cComponents = 0;
static volatile LONG g_cServerLocks = 0;
static HMODULE g_hModule = NULL;

static void LogApo(const wchar_t* format, ...)
{
    // Real-time audio thread safe: NEVER call CreateFileW/WriteFile inside audiodg.exe!
    (void)format;
}

static const CRegAPOProperties<2> sm_RegProperties(
    CLSID_PermadBLimiterAPO,
    L"PermadB Master Limiter EFX APO",
    L"Copyright (c) PermadB",
    1,
    0,
    __uuidof(IAudioProcessingObject),
    static_cast<APO_FLAG>(DEFAULT_APOREG_FLAGS | APO_FLAG_INPLACE),
    1, 1,
    1, 1,
    DEFAULT_APOREG_MAXINSTANCES,
    __uuidof(IAudioSystemEffects2)
);

static const CRegAPOProperties<2> sm_RegPropertiesSfx(
    CLSID_PermadBSfxApo,
    L"PermadB Stream Limiter SFX APO",
    L"Copyright (c) PermadB",
    1,
    0,
    __uuidof(IAudioProcessingObject),
    static_cast<APO_FLAG>(DEFAULT_APOREG_FLAGS | APO_FLAG_INPLACE),
    1, 1,
    1, 1,
    DEFAULT_APOREG_MAXINSTANCES,
    __uuidof(IAudioSystemEffects2)
);

CPermadBLimiterAPO::CPermadBLimiterAPO(IUnknown* pUnkOuter) :
    CBaseAudioProcessingObject(sm_RegProperties),
    m_nonDelegatingUnknown(this),
    m_pUnkOuter(pUnkOuter ? pUnkOuter : &m_nonDelegatingUnknown),
    m_pTelemetry(nullptr),
    m_hFile(INVALID_HANDLE_VALUE),
    m_hMapFile(NULL),
    m_hEffectsChangedEvent(NULL),
    m_lastCommandSeq(0)
{
    InterlockedIncrement(&g_cComponents);
    LogApo(L"CPermadBLimiterAPO constructor called (pUnkOuter=%p, PID %lu)", pUnkOuter, GetCurrentProcessId());
    InitTelemetry();
    LogApo(L"CPermadBLimiterAPO constructor finished (m_pTelemetry=%p)", m_pTelemetry);
}

CPermadBLimiterAPO::~CPermadBLimiterAPO()
{
    LogApo(L"CPermadBLimiterAPO destructor called (PID %lu)", GetCurrentProcessId());
    CleanupTelemetry();
    if (m_hEffectsChangedEvent != NULL)
    {
        CloseHandle(m_hEffectsChangedEvent);
        m_hEffectsChangedEvent = NULL;
    }
    InterlockedDecrement(&g_cComponents);
}

void CPermadBLimiterAPO::InitTelemetry()
{
    m_hFile = CreateFileW(
        PERMADB_SHMEM_FILE,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL
    );

    if (m_hFile != INVALID_HANDLE_VALUE)
    {
        LARGE_INTEGER size;
        size.QuadPart = sizeof(PermadBApoTelemetry);
        SetFilePointerEx(m_hFile, size, NULL, FILE_BEGIN);
        SetEndOfFile(m_hFile);

        m_hMapFile = CreateFileMappingW(
            m_hFile,
            NULL,
            PAGE_READWRITE,
            0,
            sizeof(PermadBApoTelemetry),
            PERMADB_SHMEM_NAME
        );

        if (m_hMapFile != NULL)
        {
            m_pTelemetry = reinterpret_cast<PermadBApoTelemetry*>(
                MapViewOfFile(m_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PermadBApoTelemetry))
            );

            if (m_pTelemetry != nullptr)
            {
                // If file already existed and has a valid command from the App:
                if (m_pTelemetry->magic == PERMADB_TELEMETRY_MAGIC &&
                    m_pTelemetry->targetCeilingLinear >= 0.001f &&
                    m_pTelemetry->targetCeilingLinear <= 1.0f)
                {
                    m_limiter.SetCeilingLinear(m_pTelemetry->targetCeilingLinear);
                    m_limiter.SetEnabled(m_pTelemetry->isEnabled != 0);
                    m_lastCommandSeq = m_pTelemetry->commandSeq;
                }
                else
                {
                    m_pTelemetry->targetCeilingLinear = 0.891250938f;
                    m_pTelemetry->targetCeilingDbfs = -1.0f;
                    m_pTelemetry->isEnabled = 1;
                    m_pTelemetry->commandSeq = 1;
                    m_lastCommandSeq = 1;
                }

                m_pTelemetry->magic = PERMADB_TELEMETRY_MAGIC;
                m_pTelemetry->version = 2;
                m_pTelemetry->audiodgPid = GetCurrentProcessId();
                m_pTelemetry->sampleRate = 48000;
                m_pTelemetry->channelCount = 2;
                m_pTelemetry->limiterActivations = 0;
                m_pTelemetry->processCount = 0;
                m_pTelemetry->totalFramesProcessed = 0;
                m_pTelemetry->configuredCeilingDbfs = m_limiter.GetCeilingDbfs();
                m_pTelemetry->configuredCeilingLinear = m_limiter.GetCeilingLinear();
                m_pTelemetry->lastPeakInLinear = 0.0f;
                m_pTelemetry->lastPeakOutLinear = 0.0f;
                m_pTelemetry->lastPeakInDbfs = -96.0f;
                m_pTelemetry->lastPeakOutDbfs = -96.0f;
                m_pTelemetry->maxObservedInDbfs = -96.0f;
                m_pTelemetry->maxObservedOutDbfs = -96.0f;
                m_pTelemetry->currentGainReductionDb = 0.0f;
                m_pTelemetry->maxGainReductionDb = 0.0f;
                m_pTelemetry->lastProcessTick = GetTickCount64();
                LogApo(L"Telemetry initialized via telemetry.dat (PID %lu, Phase 2 v2, ceiling=%.1f%%)",
                    GetCurrentProcessId(), m_limiter.GetCeilingLinear() * 100.0f);
            }
            else
            {
                LogApo(L"InitTelemetry: MapViewOfFile failed with error %lu", GetLastError());
            }
        }
        else
        {
            LogApo(L"InitTelemetry: CreateFileMappingW failed with error %lu", GetLastError());
        }
    }
    else
    {
        LogApo(L"InitTelemetry: CreateFileW failed with error %lu", GetLastError());
    }
}

void CPermadBLimiterAPO::CleanupTelemetry()
{
    if (m_pTelemetry != nullptr)
    {
        UnmapViewOfFile(m_pTelemetry);
        m_pTelemetry = nullptr;
    }
    if (m_hMapFile != NULL)
    {
        CloseHandle(m_hMapFile);
        m_hMapFile = NULL;
    }
    if (m_hFile != INVALID_HANDLE_VALUE)
    {
        CloseHandle(m_hFile);
        m_hFile = INVALID_HANDLE_VALUE;
    }
}

void CPermadBLimiterAPO::UpdateTelemetry(UINT32 u32Frames, UINT32 u32Channels, FLOAT32 lastInLinear, FLOAT32 lastOutLinear)
{
    if (!m_pTelemetry) return;

    CPermadBLimiter::Metrics m = m_limiter.GetMetrics(lastInLinear, lastOutLinear);

    m_pTelemetry->sampleRate = static_cast<UINT32>(GetFramesPerSecond());
    m_pTelemetry->channelCount = u32Channels;
    m_pTelemetry->limiterActivations = 0;
    m_pTelemetry->processCount++;
    m_pTelemetry->totalFramesProcessed += u32Frames;
    m_pTelemetry->configuredCeilingDbfs = m.configuredCeilingDbfs;
    m_pTelemetry->configuredCeilingLinear = m.configuredCeilingLinear;
    m_pTelemetry->lastPeakInLinear = m.lastPeakInLinear;
    m_pTelemetry->lastPeakOutLinear = m.lastPeakOutLinear;
    m_pTelemetry->lastPeakInDbfs = m.lastPeakInDbfs;
    m_pTelemetry->lastPeakOutDbfs = m.lastPeakOutDbfs;
    m_pTelemetry->maxObservedInDbfs = m.maxObservedInDbfs;
    m_pTelemetry->maxObservedOutDbfs = m.maxObservedOutDbfs;
    m_pTelemetry->currentGainReductionDb = m.currentGainReductionDb;
    m_pTelemetry->maxGainReductionDb = m.maxGainReductionDb;
    m_pTelemetry->lastProcessTick = GetTickCount64();
}

// --------------------------------------------------------------------------
// CNonDelegatingUnknown (Controls lifetime and interface exposure)
// --------------------------------------------------------------------------
CNonDelegatingUnknown::CNonDelegatingUnknown(CPermadBLimiterAPO* pOwner) :
    m_pOwner(pOwner),
    m_cRef(1)
{
}

STDMETHODIMP CNonDelegatingUnknown::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    WCHAR iidStr[64] = { 0 };
    StringFromGUID2(riid, iidStr, 64);

    if (riid == IID_IUnknown)
    {
        *ppv = static_cast<IUnknown*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObject))
    {
        *ppv = static_cast<IAudioProcessingObject*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
    {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioProcessingObjectRT))
    {
        *ppv = static_cast<IAudioProcessingObjectRT*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioSystemEffects))
    {
        *ppv = static_cast<IAudioSystemEffects*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioSystemEffects2))
    {
        *ppv = static_cast<IAudioSystemEffects2*>(m_pOwner);
    }
    else
    {
        LogApo(L"NonDelegating QI: %s -> E_NOINTERFACE", iidStr);
        return E_NOINTERFACE;
    }

    LogApo(L"NonDelegating QI: %s -> S_OK", iidStr);
    reinterpret_cast<IUnknown*>(*ppv)->AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) CNonDelegatingUnknown::AddRef()
{
    ULONG c = InterlockedIncrement(&m_cRef);
    LogApo(L"NonDelegating AddRef -> %lu", c);
    return c;
}

STDMETHODIMP_(ULONG) CNonDelegatingUnknown::Release()
{
    ULONG c = InterlockedDecrement(&m_cRef);
    LogApo(L"NonDelegating Release -> %lu", c);
    if (c == 0)
    {
        delete m_pOwner;
    }
    return c;
}

// --------------------------------------------------------------------------
// Delegating IUnknown (delegates to m_pUnkOuter)
// --------------------------------------------------------------------------
STDMETHODIMP CPermadBLimiterAPO::QueryInterface(REFIID riid, void** ppv)
{
    return m_pUnkOuter->QueryInterface(riid, ppv);
}

STDMETHODIMP_(ULONG) CPermadBLimiterAPO::AddRef()
{
    return m_pUnkOuter->AddRef();
}

STDMETHODIMP_(ULONG) CPermadBLimiterAPO::Release()
{
    return m_pUnkOuter->Release();
}

// IAudioProcessingObject
STDMETHODIMP CPermadBLimiterAPO::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps)
{
    if (!ppRegProps) return E_POINTER;
    *ppRegProps = nullptr;

    const size_t totalBytes = sizeof(sm_RegProperties);
    APO_REG_PROPERTIES* pCopy = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(totalBytes));
    if (!pCopy) return E_OUTOFMEMORY;

    memcpy(pCopy, &sm_RegProperties, totalBytes);
    *ppRegProps = pCopy;
    return S_OK;
}

static bool IsSupportedAudioFormat(const WAVEFORMATEX* pWfx)
{
    if (!pWfx) return false;
    bool isFloat = false;
    if (pWfx->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
    {
        isFloat = true;
    }
    else if (pWfx->wFormatTag == WAVE_FORMAT_EXTENSIBLE && pWfx->cbSize >= (sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)))
    {
        const WAVEFORMATEXTENSIBLE* pExt = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(pWfx);
        if (IsEqualGUID(pExt->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
        {
            isFloat = true;
        }
    }
    if (!isFloat) return false;
    if (pWfx->nChannels < 1 || pWfx->nChannels > 16) return false;
    if (pWfx->wBitsPerSample != 32) return false;
    if (pWfx->nSamplesPerSec < 8000 || pWfx->nSamplesPerSec > 384000) return false;
    return true;
}

static bool AreAudioFormatsCompatible(const WAVEFORMATEX* p1, const WAVEFORMATEX* p2)
{
    if (!p1 || !p2) return false;
    if (p1->nChannels != p2->nChannels) return false;
    if (p1->nSamplesPerSec != p2->nSamplesPerSec) return false;
    if (p1->wBitsPerSample != p2->wBitsPerSample) return false;
    return true;
}

STDMETHODIMP CPermadBLimiterAPO::IsInputFormatSupported(
    IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat,
    IAudioMediaType** ppSupportedInputFormat)
{
    if (!pRequestedInputFormat) return E_POINTER;
    if (ppSupportedInputFormat) *ppSupportedInputFormat = nullptr;

    const WAVEFORMATEX* pInWfx = pRequestedInputFormat->GetAudioFormat();
    if (!IsSupportedAudioFormat(pInWfx)) return APOERR_FORMAT_NOT_SUPPORTED;

    if (pOutputFormat)
    {
        const WAVEFORMATEX* pOutWfx = pOutputFormat->GetAudioFormat();
        if (pOutWfx && !AreAudioFormatsCompatible(pInWfx, pOutWfx))
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    return S_OK;
}

STDMETHODIMP CPermadBLimiterAPO::IsOutputFormatSupported(
    IAudioMediaType* pInputFormat,
    IAudioMediaType* pRequestedOutputFormat,
    IAudioMediaType** ppSupportedOutputFormat)
{
    if (!pRequestedOutputFormat) return E_POINTER;
    if (ppSupportedOutputFormat) *ppSupportedOutputFormat = nullptr;

    const WAVEFORMATEX* pOutWfx = pRequestedOutputFormat->GetAudioFormat();
    if (!IsSupportedAudioFormat(pOutWfx)) return APOERR_FORMAT_NOT_SUPPORTED;

    if (pInputFormat)
    {
        const WAVEFORMATEX* pInWfx = pInputFormat->GetAudioFormat();
        if (pInWfx && !AreAudioFormatsCompatible(pInWfx, pOutWfx))
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    return S_OK;
}

STDMETHODIMP CPermadBLimiterAPO::Initialize(UINT32 cbDataSize, BYTE* pbyData)
{
    LogApo(L"Initialize called (cbDataSize=%u)", cbDataSize);
    if (cbDataSize != 0 && pbyData == nullptr) return E_INVALIDARG;
    if (cbDataSize == 0 && pbyData != nullptr) return E_INVALIDARG;

    m_bIsInitialized = true;
    LogApo(L"Initialize succeeded");
    return S_OK;
}

STDMETHODIMP CPermadBLimiterAPO::Reset()
{
    LogApo(L"Reset called");
    m_limiter.Reset();
    return CBaseAudioProcessingObject::Reset();
}

// IAudioProcessingObjectConfiguration
STDMETHODIMP CPermadBLimiterAPO::LockForProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    LogApo(L"LockForProcess called (inConns=%u, outConns=%u)", u32NumInputConnections, u32NumOutputConnections);
    HRESULT hr = CBaseAudioProcessingObject::LockForProcess(
        u32NumInputConnections,
        ppInputConnections,
        u32NumOutputConnections,
        ppOutputConnections
    );
    if (SUCCEEDED(hr))
    {
        uint32_t sampleRate = static_cast<uint32_t>(GetFramesPerSecond());
        uint32_t channels = static_cast<uint32_t>(GetSamplesPerFrame());
        if (channels < 1) channels = 1;
        if (channels > 16) channels = 16;
        if (sampleRate < 8000) sampleRate = 48000;
        m_limiter.Init(sampleRate, channels);

        if (m_pTelemetry && m_pTelemetry->targetCeilingLinear >= 0.001f && m_pTelemetry->targetCeilingLinear <= 1.0f)
        {
            float effCeiling = m_pTelemetry->targetCeilingLinear;
            float masterSafe = m_pTelemetry->masterSafetyCeilingLinear;
            if (masterSafe >= 0.001f && masterSafe <= 1.0f && masterSafe < effCeiling)
            {
                effCeiling = masterSafe;
            }
            m_limiter.SetCeilingLinear(effCeiling);
            m_limiter.SetEnabled(m_pTelemetry->isEnabled != 0);
            m_lastCommandSeq = m_pTelemetry->commandSeq;
            LogApo(L"LockForProcess initialized ceiling to %.1f%% (%.4f linear, seq=%lu)",
                effCeiling * 100.0f, effCeiling, m_lastCommandSeq);
        }
    }
    LogApo(L"LockForProcess result: 0x%08X (rate=%.0f, channels=%u, m_bIsLocked=%d, lookaheadFrames=%u)",
        hr, GetFramesPerSecond(), GetSamplesPerFrame(), m_bIsLocked ? 1 : 0, m_limiter.GetLookaheadFrames());
    return hr;
}

STDMETHODIMP CPermadBLimiterAPO::UnlockForProcess()
{
    LogApo(L"UnlockForProcess called");
    m_limiter.Reset();
    return CBaseAudioProcessingObject::UnlockForProcess();
}

// IAudioProcessingObjectRT
STDMETHODIMP_(void) CPermadBLimiterAPO::APOProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_PROPERTY** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    if (!m_bIsLocked || u32NumInputConnections == 0 || u32NumOutputConnections == 0)
        return;

    APO_CONNECTION_PROPERTY* pIn = ppInputConnections[0];
    APO_CONNECTION_PROPERTY* pOut = ppOutputConnections[0];

    if (!pIn || !pOut || !pIn->pBuffer || !pOut->pBuffer)
        return;

    if (m_pTelemetry)
    {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->efxProcessCount));
    }

    UINT32 u32Frames = pIn->u32ValidFrameCount;
    UINT32 u32Channels = GetSamplesPerFrame();
    if (u32Channels < 1) u32Channels = 1;
    if (u32Channels > 16) u32Channels = 16;

    FLOAT32* pf32In = reinterpret_cast<FLOAT32*>(pIn->pBuffer);
    FLOAT32* pf32Out = reinterpret_cast<FLOAT32*>(pOut->pBuffer);

    if (pIn->u32BufferFlags == BUFFER_SILENT)
    {
        // Drain the 5ms lookahead delay buffer smoothly instead of zeroing it out abruptly
        // (prevents 5ms static/pop discontinuities when Windows marks brief buffers silent!)
        ZeroMemory(pf32In, sizeof(FLOAT32) * u32Frames * u32Channels);
        m_limiter.Process(pf32In, pf32Out, u32Frames);
        FLOAT32 silentOutPeak = 0.0f;
        for (UINT32 i = 0; i < u32Frames * u32Channels; ++i)
        {
            FLOAT32 a = std::abs(pf32Out[i]);
            if (a > silentOutPeak) silentOutPeak = a;
        }
        pOut->u32ValidFrameCount = u32Frames;
        pOut->u32BufferFlags = (silentOutPeak < 0.00001f) ? BUFFER_SILENT : BUFFER_VALID;
        return;
    }

    // Measure input peak of this buffer for telemetry
    FLOAT32 peakIn = 0.0f;
    UINT32 totalSamples = u32Frames * u32Channels;
    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        FLOAT32 absIn = std::abs(pf32In[i]);
        if (absIn > peakIn) peakIn = absIn;
    }

    // Dynamic ceiling control from PermadB App
    if (m_pTelemetry)
    {
        UINT32 currentSeq = m_pTelemetry->commandSeq;
        if (currentSeq != m_lastCommandSeq)
        {
            m_lastCommandSeq = currentSeq;
            float targetLin = m_pTelemetry->targetCeilingLinear;
            bool enabled = (m_pTelemetry->isEnabled != 0);

            if (targetLin >= 0.001f && targetLin <= 1.0f)
            {
                float effCeiling = targetLin;
                float masterSafe = m_pTelemetry->masterSafetyCeilingLinear;
                if (masterSafe >= 0.001f && masterSafe <= 1.0f && masterSafe < effCeiling)
                {
                    effCeiling = masterSafe;
                }
                m_limiter.SetCeilingLinear(effCeiling);
                m_limiter.SetEnabled(enabled);
                LogApo(L"Dynamic ceiling updated: %.1f%% (%.4f linear, %.2fdBFS, enabled=%d)",
                    effCeiling * 100.0f, effCeiling, m_limiter.GetCeilingDbfs(), enabled ? 1 : 0);
            }
        }
    }

    // Phase 2: Real Lookahead Brickwall Safety Limiter
    // Operates directly on PCM samples; zero real-time heap allocations.
    m_limiter.Process(pf32In, pf32Out, u32Frames);

    // Measure output peak of this buffer for telemetry
    FLOAT32 peakOut = 0.0f;
    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        FLOAT32 absOut = std::abs(pf32Out[i]);
        if (absOut > peakOut) peakOut = absOut;
    }

    pOut->u32ValidFrameCount = u32Frames;
    pOut->u32BufferFlags = BUFFER_VALID;

    // Update real-time telemetry
    UpdateTelemetry(u32Frames, u32Channels, peakIn, peakOut);

    if (m_pTelemetry)
    {
        UINT64 cnt = m_pTelemetry->processCount;
        if (cnt <= 10 || (cnt % 5000 == 0))
        {
            LogApo(L"APOProcess [%llu]: frames=%u, in=%.2fdB, out=%.2fdB, maxOut=%.2fdB, maxGR=%.2fdB, act=%lu",
                cnt, u32Frames, m_pTelemetry->lastPeakInDbfs, m_pTelemetry->lastPeakOutDbfs,
                m_pTelemetry->maxObservedOutDbfs, m_pTelemetry->maxGainReductionDb, m_pTelemetry->limiterActivations);
        }
    }
}

// IAudioSystemEffects2
STDMETHODIMP CPermadBLimiterAPO::GetEffectsList(
    _Outptr_result_buffer_maybenull_(*pcEffects) LPGUID* ppEffectsIds,
    _Out_ UINT* pcEffects,
    _In_ HANDLE Event)
{
    LogApo(L"GetEffectsList called (Event=%p)", Event);

    if (!ppEffectsIds || !pcEffects) return E_POINTER;

    if (Event != NULL)
    {
        if (m_hEffectsChangedEvent != NULL)
        {
            CloseHandle(m_hEffectsChangedEvent);
            m_hEffectsChangedEvent = NULL;
        }
        DuplicateHandle(GetCurrentProcess(), Event, GetCurrentProcess(), &m_hEffectsChangedEvent, EVENT_MODIFY_STATE, FALSE, 0);
    }

    *ppEffectsIds = nullptr;
    *pcEffects = 0;
    return S_OK;
}

// --------------------------------------------------------------------------
// COM Class Factory Implementation (EFX)
// --------------------------------------------------------------------------
class CPermadBLimiterClassFactory : public IClassFactory
{
private:
    volatile LONG m_cRef;
public:
    CPermadBLimiterClassFactory() : m_cRef(1) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG ul = InterlockedDecrement(&m_cRef);
        if (ul == 0) delete this;
        return ul;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;

        if (pUnkOuter != nullptr && riid != IID_IUnknown)
        {
            return E_NOINTERFACE;
        }

        CPermadBLimiterAPO* pApo = new (std::nothrow) CPermadBLimiterAPO(pUnkOuter);
        if (!pApo) return E_OUTOFMEMORY;

        HRESULT hr;
        if (pUnkOuter != nullptr)
        {
            *ppv = static_cast<IUnknown*>(pApo->GetNonDelegatingUnknown());
            hr = S_OK;
        }
        else
        {
            hr = pApo->GetNonDelegatingUnknown()->QueryInterface(riid, ppv);
            pApo->GetNonDelegatingUnknown()->Release();
        }
        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock) InterlockedIncrement(&g_cServerLocks);
        else InterlockedDecrement(&g_cServerLocks);
        return S_OK;
    }
};

// --------------------------------------------------------------------------
// CPermadBSfxApo Implementation (Stage 1: Pre-Mix Per-Stream Limiter)
// --------------------------------------------------------------------------
// Canonical Windows SDK ksmedia.h GUIDs + aliases
static const GUID PermadB_MODE_DEFAULT =
    { 0xc18e2f7e, 0x933d, 0x4965, { 0xb7, 0xd1, 0x1e, 0xef, 0x22, 0x8d, 0x2a, 0xf3 } };
static const GUID PermadB_MODE_COMMUNICATIONS =
    { 0x98951333, 0xb9cd, 0x48b1, { 0xa0, 0xa3, 0xff, 0x40, 0x68, 0x2d, 0x73, 0xf7 } };
static const GUID PermadB_MODE_SPEECH =
    { 0xfc1cfc9b, 0xb9d6, 0x4cfa, { 0xb5, 0xe0, 0x4b, 0xb2, 0x16, 0x68, 0x78, 0xb2 } };
static const GUID PermadB_MODE_SPEECH_ALT =
    { 0xfc1cf0a4, 0x3860, 0x4592, { 0x82, 0x32, 0xa5, 0x67, 0x84, 0x7b, 0xe2, 0xd1 } };
static const GUID PermadB_MODE_MEDIA =
    { 0x4780004e, 0x7133, 0x41d8, { 0x8c, 0x74, 0x66, 0x0d, 0xad, 0xd2, 0xc0, 0xee } };
static const GUID PermadB_MODE_MEDIA_ALT =
    { 0x47800040, 0x4533, 0x461b, { 0x9b, 0x4c, 0x9e, 0x9d, 0x77, 0xb1, 0x10, 0x57 } };
static const GUID PermadB_MODE_MOVIE =
    { 0xb26feb0d, 0xec94, 0x477c, { 0x94, 0x94, 0xd1, 0xab, 0x8e, 0x75, 0x3f, 0x6e } };
static const GUID PermadB_MODE_MOVIE_ALT =
    { 0xb26fedb9, 0x886c, 0x430b, { 0x80, 0x18, 0xcb, 0x54, 0x67, 0xe9, 0xfc, 0x0e } };

CPermadBSfxApo::CPermadBSfxApo(IUnknown* pUnkOuter) :
    CBaseAudioProcessingObject(sm_RegPropertiesSfx),
    m_nonDelegatingUnknown(this),
    m_pUnkOuter(pUnkOuter ? pUnkOuter : &m_nonDelegatingUnknown),
    m_pTelemetry(nullptr),
    m_hFile(INVALID_HANDLE_VALUE),
    m_hMapFile(NULL),
    m_hEffectsChangedEvent(NULL),
    m_slotIndex(-1),
    m_instanceId(0),
    m_audioProcessingMode(PermadBMode_Default)
{
    InterlockedIncrement(&g_cComponents);
    InitTelemetry();
}

CPermadBSfxApo::~CPermadBSfxApo()
{
    ReleaseSlot();
    CleanupTelemetry();
    if (m_hEffectsChangedEvent != NULL)
    {
        CloseHandle(m_hEffectsChangedEvent);
        m_hEffectsChangedEvent = NULL;
    }
    InterlockedDecrement(&g_cComponents);
}

void CPermadBSfxApo::InitTelemetry()
{
    m_hFile = CreateFileW(
        PERMADB_SHMEM_FILE,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL
    );

    if (m_hFile != INVALID_HANDLE_VALUE)
    {
        LARGE_INTEGER size;
        size.QuadPart = sizeof(PermadBApoTelemetry);
        SetFilePointerEx(m_hFile, size, NULL, FILE_BEGIN);
        SetEndOfFile(m_hFile);

        m_hMapFile = CreateFileMappingW(
            m_hFile,
            NULL,
            PAGE_READWRITE,
            0,
            sizeof(PermadBApoTelemetry),
            PERMADB_SHMEM_NAME
        );

        if (m_hMapFile != NULL)
        {
            m_pTelemetry = reinterpret_cast<PermadBApoTelemetry*>(
                MapViewOfFile(m_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PermadBApoTelemetry))
            );
        }
    }
}

void CPermadBSfxApo::CleanupTelemetry()
{
    if (m_pTelemetry != nullptr)
    {
        UnmapViewOfFile(m_pTelemetry);
        m_pTelemetry = nullptr;
    }
    if (m_hMapFile != NULL)
    {
        CloseHandle(m_hMapFile);
        m_hMapFile = NULL;
    }
    if (m_hFile != INVALID_HANDLE_VALUE)
    {
        CloseHandle(m_hFile);
        m_hFile = INVALID_HANDLE_VALUE;
    }
}

int CPermadBSfxApo::ClaimSlot(UINT32 mode)
{
    if (!m_pTelemetry) return -1;

    for (int i = 0; i < PERMADB_SFX_MAX_SLOTS; ++i)
    {
        if (InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&m_pTelemetry->slots[i].active), 1, 0) == 0)
        {
            static volatile LONG s_instanceCounter = 1000;
            UINT32 instId = static_cast<UINT32>(InterlockedIncrement(&s_instanceCounter));
            m_instanceId = instId;
            m_pTelemetry->slots[i].instanceId = instId;
            m_pTelemetry->slots[i].audioProcessingMode = mode;
            m_pTelemetry->slots[i].inputPeak = 0.0f;
            m_pTelemetry->slots[i].outputPeak = 0.0f;

            // Fail-safe default on sample 0: Safe Ceiling (-16.0 dBFS / 0.1585 linear)
            float safeCeiling = 0.1585f;
            if (m_pTelemetry->targetCeilingLinear >= 0.001f && m_pTelemetry->targetCeilingLinear <= 1.0f)
            {
                safeCeiling = m_pTelemetry->targetCeilingLinear;
            }
            m_pTelemetry->slots[i].targetCeilingLinear = safeCeiling;
            // Every stream begins in active limiting mode until C# correlation sets bypass per user rules
            m_pTelemetry->slots[i].bypass = 0;
            m_pTelemetry->slots[i].matchedProcessId = 0;

            InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->sfxActiveCount));
            return i;
        }
    }
    return -1;
}

void CPermadBSfxApo::ReleaseSlot()
{
    if (!m_pTelemetry || m_slotIndex < 0 || m_slotIndex >= PERMADB_SFX_MAX_SLOTS) return;

    m_pTelemetry->slots[m_slotIndex].inputPeak = 0.0f;
    m_pTelemetry->slots[m_slotIndex].outputPeak = 0.0f;
    m_pTelemetry->slots[m_slotIndex].matchedProcessId = 0;
    m_pTelemetry->slots[m_slotIndex].bypass = 0;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_pTelemetry->slots[m_slotIndex].active), 0);
    InterlockedDecrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->sfxActiveCount));
    m_slotIndex = -1;
}

// CNonDelegatingUnknownSfx
CNonDelegatingUnknownSfx::CNonDelegatingUnknownSfx(CPermadBSfxApo* pOwner) :
    m_pOwner(pOwner),
    m_cRef(1)
{
}

STDMETHODIMP CNonDelegatingUnknownSfx::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown)
    {
        *ppv = static_cast<IUnknown*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObject))
    {
        *ppv = static_cast<IAudioProcessingObject*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
    {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioProcessingObjectRT))
    {
        *ppv = static_cast<IAudioProcessingObjectRT*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioSystemEffects))
    {
        *ppv = static_cast<IAudioSystemEffects*>(m_pOwner);
    }
    else if (riid == __uuidof(IAudioSystemEffects2))
    {
        *ppv = static_cast<IAudioSystemEffects2*>(m_pOwner);
    }
    else
    {
        return E_NOINTERFACE;
    }

    reinterpret_cast<IUnknown*>(*ppv)->AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) CNonDelegatingUnknownSfx::AddRef()
{
    return InterlockedIncrement(&m_cRef);
}

STDMETHODIMP_(ULONG) CNonDelegatingUnknownSfx::Release()
{
    ULONG c = InterlockedDecrement(&m_cRef);
    if (c == 0)
    {
        delete m_pOwner;
    }
    return c;
}

// Delegating IUnknown for CPermadBSfxApo
STDMETHODIMP CPermadBSfxApo::QueryInterface(REFIID riid, void** ppv)
{
    return m_pUnkOuter->QueryInterface(riid, ppv);
}

STDMETHODIMP_(ULONG) CPermadBSfxApo::AddRef()
{
    return m_pUnkOuter->AddRef();
}

STDMETHODIMP_(ULONG) CPermadBSfxApo::Release()
{
    return m_pUnkOuter->Release();
}

// IAudioProcessingObject (SFX)
STDMETHODIMP CPermadBSfxApo::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps)
{
    if (!ppRegProps) return E_POINTER;
    *ppRegProps = nullptr;

    const size_t totalBytes = sizeof(sm_RegPropertiesSfx);
    APO_REG_PROPERTIES* pCopy = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(totalBytes));
    if (!pCopy) return E_OUTOFMEMORY;

    memcpy(pCopy, &sm_RegPropertiesSfx, totalBytes);
    *ppRegProps = pCopy;
    return S_OK;
}

STDMETHODIMP CPermadBSfxApo::IsInputFormatSupported(
    IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat,
    IAudioMediaType** ppSupportedInputFormat)
{
    if (!pRequestedInputFormat) return E_POINTER;
    if (ppSupportedInputFormat) *ppSupportedInputFormat = nullptr;

    const WAVEFORMATEX* pInWfx = pRequestedInputFormat->GetAudioFormat();
    if (!IsSupportedAudioFormat(pInWfx)) return APOERR_FORMAT_NOT_SUPPORTED;

    if (pOutputFormat)
    {
        const WAVEFORMATEX* pOutWfx = pOutputFormat->GetAudioFormat();
        if (pOutWfx && !AreAudioFormatsCompatible(pInWfx, pOutWfx))
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    return S_OK;
}

STDMETHODIMP CPermadBSfxApo::IsOutputFormatSupported(
    IAudioMediaType* pInputFormat,
    IAudioMediaType* pRequestedOutputFormat,
    IAudioMediaType** ppSupportedOutputFormat)
{
    if (!pRequestedOutputFormat) return E_POINTER;
    if (ppSupportedOutputFormat) *ppSupportedOutputFormat = nullptr;

    const WAVEFORMATEX* pOutWfx = pRequestedOutputFormat->GetAudioFormat();
    if (!IsSupportedAudioFormat(pOutWfx)) return APOERR_FORMAT_NOT_SUPPORTED;

    if (pInputFormat)
    {
        const WAVEFORMATEX* pInWfx = pInputFormat->GetAudioFormat();
        if (pInWfx && !AreAudioFormatsCompatible(pInWfx, pOutWfx))
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    return S_OK;
}

STDMETHODIMP CPermadBSfxApo::Initialize(UINT32 cbDataSize, BYTE* pbyData)
{
    if (cbDataSize != 0 && pbyData == nullptr) return E_INVALIDARG;
    if (cbDataSize == 0 && pbyData != nullptr) return E_INVALIDARG;

    UINT32 mode = PermadBMode_Default;
    if (pbyData != nullptr && cbDataSize >= sizeof(APOInitSystemEffects))
    {
        // Tolerant parsing for APOInitSystemEffects2 and APOInitSystemEffects3
        if (cbDataSize >= sizeof(APOInitSystemEffects2))
        {
            APOInitSystemEffects2* pInit2 = reinterpret_cast<APOInitSystemEffects2*>(pbyData);
            GUID m = pInit2->AudioProcessingMode;
            if (IsEqualGUID(m, PermadB_MODE_COMMUNICATIONS) ||
                IsEqualGUID(m, PermadB_MODE_SPEECH) ||
                IsEqualGUID(m, PermadB_MODE_SPEECH_ALT))
            {
                mode = PermadBMode_Communications;
            }
            else if (IsEqualGUID(m, PermadB_MODE_MEDIA) ||
                     IsEqualGUID(m, PermadB_MODE_MEDIA_ALT) ||
                     IsEqualGUID(m, PermadB_MODE_MOVIE) ||
                     IsEqualGUID(m, PermadB_MODE_MOVIE_ALT))
            {
                mode = PermadBMode_Media;
            }
            else
            {
                mode = PermadBMode_Default;
            }
        }
    }
    m_audioProcessingMode = mode;

    if (!m_pTelemetry)
    {
        InitTelemetry();
    }

    if (m_pTelemetry)
    {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->sfxInitCount));
    }

    if (m_slotIndex < 0 && m_pTelemetry)
    {
        m_slotIndex = ClaimSlot(mode);
    }

    m_bIsInitialized = true;
    return S_OK;
}

STDMETHODIMP CPermadBSfxApo::Reset()
{
    m_limiter.Reset();
    return CBaseAudioProcessingObject::Reset();
}

STDMETHODIMP CPermadBSfxApo::LockForProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    if (m_pTelemetry)
    {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->sfxLockCount));
    }

    HRESULT hr = CBaseAudioProcessingObject::LockForProcess(
        u32NumInputConnections,
        ppInputConnections,
        u32NumOutputConnections,
        ppOutputConnections
    );
    if (SUCCEEDED(hr))
    {
        uint32_t sampleRate = static_cast<uint32_t>(GetFramesPerSecond());
        uint32_t channels = static_cast<uint32_t>(GetSamplesPerFrame());
        if (channels < 1) channels = 1;
        if (channels > 16) channels = 16;
        if (sampleRate < 8000) sampleRate = 48000;

        if (m_pTelemetry)
        {
            m_pTelemetry->lastSfxChannels = channels;
        }

        m_limiter.Init(sampleRate, channels);

        float safeCeiling = 0.1585f;
        if (m_pTelemetry && m_pTelemetry->targetCeilingLinear >= 0.001f && m_pTelemetry->targetCeilingLinear <= 1.0f)
        {
            safeCeiling = m_pTelemetry->targetCeilingLinear;
        }
        m_limiter.SetCeilingLinear(safeCeiling);
        m_limiter.SetEnabled(true);
    }
    return hr;
}

STDMETHODIMP CPermadBSfxApo::UnlockForProcess()
{
    m_limiter.Reset();
    return CBaseAudioProcessingObject::UnlockForProcess();
}

// IAudioProcessingObjectRT (SFX)
STDMETHODIMP_(void) CPermadBSfxApo::APOProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_PROPERTY** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    if (!m_bIsLocked || u32NumInputConnections == 0 || u32NumOutputConnections == 0)
        return;

    APO_CONNECTION_PROPERTY* pIn = ppInputConnections[0];
    APO_CONNECTION_PROPERTY* pOut = ppOutputConnections[0];

    if (!pIn || !pOut || !pIn->pBuffer || !pOut->pBuffer)
        return;

    if (m_pTelemetry)
    {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pTelemetry->sfxProcessCount));
    }

    // Lazy slot claiming if shared memory was created after Initialize
    if (m_slotIndex < 0)
    {
        if (!m_pTelemetry) InitTelemetry();
        if (m_pTelemetry) m_slotIndex = ClaimSlot(m_audioProcessingMode);
    }

    UINT32 u32Frames = pIn->u32ValidFrameCount;
    UINT32 u32Channels = GetSamplesPerFrame();
    if (u32Channels < 1) u32Channels = 1;
    if (u32Channels > 16) u32Channels = 16;
    FLOAT32* pf32In = reinterpret_cast<FLOAT32*>(pIn->pBuffer);
    FLOAT32* pf32Out = reinterpret_cast<FLOAT32*>(pOut->pBuffer);

    if (pIn->u32BufferFlags == BUFFER_SILENT)
    {
        ZeroMemory(pf32In, sizeof(FLOAT32) * u32Frames * u32Channels);
        m_limiter.Process(pf32In, pf32Out, u32Frames);
        FLOAT32 silentOutPeak = 0.0f;
        for (UINT32 i = 0; i < u32Frames * u32Channels; ++i)
        {
            FLOAT32 a = std::abs(pf32Out[i]);
            if (a > silentOutPeak) silentOutPeak = a;
        }
        pOut->u32ValidFrameCount = u32Frames;
        pOut->u32BufferFlags = (silentOutPeak < 0.00001f) ? BUFFER_SILENT : BUFFER_VALID;
        if (m_pTelemetry && m_slotIndex >= 0 && m_slotIndex < PERMADB_SFX_MAX_SLOTS)
        {
            m_pTelemetry->slots[m_slotIndex].inputPeak *= 0.85f;
            m_pTelemetry->slots[m_slotIndex].outputPeak *= 0.85f;
        }
        return;
    }

    FLOAT32 peakIn = 0.0f;
    UINT32 totalSamples = u32Frames * u32Channels;
    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        FLOAT32 absIn = std::abs(pf32In[i]);
        if (absIn > peakIn) peakIn = absIn;
    }

    if (m_pTelemetry && m_slotIndex >= 0 && m_slotIndex < PERMADB_SFX_MAX_SLOTS)
    {
        float targetLin = m_pTelemetry->slots[m_slotIndex].targetCeilingLinear;
        uint32_t bypass = m_pTelemetry->slots[m_slotIndex].bypass;
        uint32_t globalEnabled = m_pTelemetry->isEnabled;

        if (globalEnabled == 0 || bypass != 0)
        {
            m_limiter.SetEnabled(false);
        }
        else
        {
            if (targetLin >= 0.001f && targetLin <= 1.0f)
            {
                m_limiter.SetCeilingLinear(targetLin);
            }
            m_limiter.SetEnabled(true);
        }
    }

    m_limiter.Process(pf32In, pf32Out, u32Frames);

    FLOAT32 peakOut = 0.0f;
    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        FLOAT32 absOut = std::abs(pf32Out[i]);
        if (absOut > peakOut) peakOut = absOut;
    }

    pOut->u32ValidFrameCount = u32Frames;
    pOut->u32BufferFlags = BUFFER_VALID;

    if (m_pTelemetry && m_slotIndex >= 0 && m_slotIndex < PERMADB_SFX_MAX_SLOTS)
    {
        float prevIn = m_pTelemetry->slots[m_slotIndex].inputPeak;
        float prevOut = m_pTelemetry->slots[m_slotIndex].outputPeak;
        m_pTelemetry->slots[m_slotIndex].inputPeak = (std::max)(peakIn, prevIn * 0.85f);
        m_pTelemetry->slots[m_slotIndex].outputPeak = (std::max)(peakOut, prevOut * 0.85f);
    }
}

STDMETHODIMP CPermadBSfxApo::GetEffectsList(
    _Outptr_result_buffer_maybenull_(*pcEffects) LPGUID* ppEffectsIds,
    _Out_ UINT* pcEffects,
    _In_ HANDLE Event)
{
    if (!ppEffectsIds || !pcEffects) return E_POINTER;

    if (Event != NULL)
    {
        if (m_hEffectsChangedEvent != NULL)
        {
            CloseHandle(m_hEffectsChangedEvent);
            m_hEffectsChangedEvent = NULL;
        }
        DuplicateHandle(GetCurrentProcess(), Event, GetCurrentProcess(), &m_hEffectsChangedEvent, EVENT_MODIFY_STATE, FALSE, 0);
    }

    *ppEffectsIds = nullptr;
    *pcEffects = 0;
    return S_OK;
}

// --------------------------------------------------------------------------
// COM Class Factory Implementation (SFX)
// --------------------------------------------------------------------------
class CPermadBSfxClassFactory : public IClassFactory
{
private:
    volatile LONG m_cRef;
public:
    CPermadBSfxClassFactory() : m_cRef(1) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG ul = InterlockedDecrement(&m_cRef);
        if (ul == 0) delete this;
        return ul;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;

        if (pUnkOuter != nullptr && riid != IID_IUnknown)
        {
            return E_NOINTERFACE;
        }

        CPermadBSfxApo* pApo = new (std::nothrow) CPermadBSfxApo(pUnkOuter);
        if (!pApo) return E_OUTOFMEMORY;

        HRESULT hr;
        if (pUnkOuter != nullptr)
        {
            *ppv = static_cast<IUnknown*>(pApo->GetNonDelegatingUnknown());
            hr = S_OK;
        }
        else
        {
            hr = pApo->GetNonDelegatingUnknown()->QueryInterface(riid, ppv);
            pApo->GetNonDelegatingUnknown()->Release();
        }
        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock) InterlockedIncrement(&g_cServerLocks);
        else InterlockedDecrement(&g_cServerLocks);
        return S_OK;
    }
};

// --------------------------------------------------------------------------
// DLL Exports
// --------------------------------------------------------------------------
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    WCHAR clsidStr[64] = { 0 };
    WCHAR iidStr[64] = { 0 };
    StringFromGUID2(rclsid, clsidStr, 64);
    StringFromGUID2(riid, iidStr, 64);
    LogApo(L"DllGetClassObject: CLSID=%s, IID=%s", clsidStr, iidStr);

    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (rclsid == CLSID_PermadBLimiterAPO || rclsid == CLSID_PermadBEfxApo)
    {
        CPermadBLimiterClassFactory* pFactory = new (std::nothrow) CPermadBLimiterClassFactory();
        if (!pFactory) return E_OUTOFMEMORY;

        HRESULT hr = pFactory->QueryInterface(riid, ppv);
        pFactory->Release();
        LogApo(L"DllGetClassObject returning EFX 0x%08X", hr);
        return hr;
    }
    else if (rclsid == CLSID_PermadBSfxApo)
    {
        CPermadBSfxClassFactory* pFactory = new (std::nothrow) CPermadBSfxClassFactory();
        if (!pFactory) return E_OUTOFMEMORY;

        HRESULT hr = pFactory->QueryInterface(riid, ppv);
        pFactory->Release();
        LogApo(L"DllGetClassObject returning SFX 0x%08X", hr);
        return hr;
    }

    LogApo(L"DllGetClassObject: CLASS_E_CLASSNOTAVAILABLE");
    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow()
{
    return (g_cComponents == 0 && g_cServerLocks == 0) ? S_OK : S_FALSE;
}

// Registry helpers for DllRegisterServer / DllUnregisterServer
static HRESULT SetRegKeyAndValue(HKEY hRoot, LPCWSTR subKey, LPCWSTR valueName, LPCWSTR data)
{
    HKEY hKey;
    LONG lRes = RegCreateKeyExW(hRoot, subKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL);
    if (lRes != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lRes);

    if (data != nullptr)
    {
        lRes = RegSetValueExW(hKey, valueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(data), static_cast<DWORD>((wcslen(data) + 1) * sizeof(WCHAR)));
    }
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(lRes);
}

static HRESULT SetRegDword(HKEY hRoot, LPCWSTR subKey, LPCWSTR valueName, DWORD data)
{
    HKEY hKey;
    LONG lRes = RegCreateKeyExW(hRoot, subKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL);
    if (lRes != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lRes);

    lRes = RegSetValueExW(hKey, valueName, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&data), sizeof(DWORD));
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(lRes);
}

static HRESULT RegisterApoClsid(LPCWSTR clsidStr, LPCWSTR friendlyName, LPCWSTR szModule)
{
    WCHAR clsidKey[256];
    swprintf_s(clsidKey, L"CLSID\\%s", clsidStr);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, clsidKey, NULL, friendlyName);

    WCHAR inprocKey[256];
    swprintf_s(inprocKey, L"CLSID\\%s\\InProcServer32", clsidStr);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, inprocKey, NULL, szModule);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, inprocKey, L"ThreadingModel", L"Both");

    WCHAR apoKey[256];
    swprintf_s(apoKey, L"AudioEngine\\AudioProcessingObjects\\%s", clsidStr);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"FriendlyName", friendlyName);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"Copyright", L"PermadB");
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MajorVersion", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinorVersion", 0);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"Flags", 15); // DEFAULT | INPLACE
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinInputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxInputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinOutputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxOutputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxInstances", 0xFFFFFFFF);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"NumAPOInterfaces", 2);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"APOInterface0", L"{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}"); // IID_IAudioProcessingObject
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"APOInterface1", L"{BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}"); // IID_IAudioSystemEffects2
    return S_OK;
}

static HRESULT UnregisterApoClsid(LPCWSTR clsidStr)
{
    WCHAR clsidKey[256];
    swprintf_s(clsidKey, L"CLSID\\%s", clsidStr);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, clsidKey);

    WCHAR apoKey[256];
    swprintf_s(apoKey, L"AudioEngine\\AudioProcessingObjects\\%s", clsidStr);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, apoKey);
    return S_OK;
}

STDAPI DllRegisterServer()
{
    WCHAR szModule[MAX_PATH];
    if (GetModuleFileNameW(g_hModule, szModule, MAX_PATH) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    RegisterApoClsid(L"{968ff234-1895-49f1-8b97-9d9075eb25b6}", L"PermadB Master Limiter EFX APO", szModule);
    RegisterApoClsid(L"{b41e8c71-2947-4e3a-965a-06385d8204d1}", L"PermadB Stream Limiter SFX APO", szModule);

    LogApo(L"DllRegisterServer registered EFX and SFX APOs for %s", szModule);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    UnregisterApoClsid(L"{968ff234-1895-49f1-8b97-9d9075eb25b6}");
    UnregisterApoClsid(L"{b41e8c71-2947-4e3a-965a-06385d8204d1}");

    return S_OK;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);

        WCHAR modPath[MAX_PATH] = { 0 };
        GetModuleFileNameW(NULL, modPath, MAX_PATH);
        LogApo(L"DLL_PROCESS_ATTACH into process: %s (PID %lu)", modPath, GetCurrentProcessId());
    }
    else if (ul_reason_for_call == DLL_PROCESS_DETACH)
    {
        LogApo(L"DLL_PROCESS_DETACH from PID %lu", GetCurrentProcessId());
    }
    return TRUE;
}
