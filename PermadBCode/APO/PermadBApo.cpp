// PermadBApo.cpp - Implementation of PermadB Master Limiter Audio Processing Object
#include "PermadBApo.h"
#include <sddl.h>
#include <algorithm>
#include <cstdio>

static volatile LONG g_cComponents = 0;
static volatile LONG g_cServerLocks = 0;
static HMODULE g_hModule = NULL;

static const CRegAPOProperties<1> sm_RegProperties(
    CLSID_PermadBLimiterAPO,
    L"PermadB Limiter APO",
    L"Copyright (c) PermadB",
    1,
    0,
    __uuidof(IAudioProcessingObjectRT),
    static_cast<APO_FLAG>(DEFAULT_APOREG_FLAGS | APO_FLAG_INPLACE),
    1, 1,
    1, 1,
    DEFAULT_APOREG_MAXINSTANCES
);

CPermadBLimiterAPO::CPermadBLimiterAPO() :
    CBaseAudioProcessingObject(sm_RegProperties),
    m_cRef(1),
    m_pTelemetry(nullptr),
    m_hMapFile(NULL),
    m_hEffectsChangedEvent(NULL)
{
    InterlockedIncrement(&g_cComponents);
    InitTelemetry();
}

CPermadBLimiterAPO::~CPermadBLimiterAPO()
{
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
    // Create shared memory with NULL DACL (allowing audiodg under LOCAL SERVICE and user tools to access)
    SECURITY_DESCRIPTOR sd;
    if (InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION))
    {
        SetSecurityDescriptorDacl(&sd, TRUE, NULL, FALSE);
        SECURITY_ATTRIBUTES sa;
        sa.nLength = sizeof(sa);
        sa.lpSecurityDescriptor = &sd;
        sa.bInheritHandle = FALSE;

        m_hMapFile = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            &sa,
            PAGE_READWRITE,
            0,
            sizeof(PermadBApoTelemetry),
            PERMADB_SHMEM_NAME
        );

        if (m_hMapFile == NULL && GetLastError() == ERROR_ALREADY_EXISTS)
        {
            m_hMapFile = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, PERMADB_SHMEM_NAME);
        }

        if (m_hMapFile != NULL)
        {
            m_pTelemetry = reinterpret_cast<PermadBApoTelemetry*>(
                MapViewOfFile(m_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PermadBApoTelemetry))
            );

            if (m_pTelemetry != nullptr)
            {
                m_pTelemetry->magic = PERMADB_TELEMETRY_MAGIC;
                m_pTelemetry->version = 1;
                m_pTelemetry->audiodgPid = GetCurrentProcessId();
                m_pTelemetry->fixedGainDb = -12.0f;
                m_pTelemetry->fixedGainLinear = 0.25118864f; // 10^(-12/20)
            }
        }
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
}

void CPermadBLimiterAPO::UpdateTelemetry(UINT32 u32Frames, UINT32 u32Channels, const FLOAT32* pf32In, const FLOAT32* pf32Out)
{
    if (!m_pTelemetry) return;

    UINT32 totalSamples = u32Frames * u32Channels;
    FLOAT32 peakIn = 0.0f;
    FLOAT32 peakOut = 0.0f;

    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        FLOAT32 absIn = std::abs(pf32In[i]);
        if (absIn > peakIn) peakIn = absIn;

        FLOAT32 absOut = std::abs(pf32Out[i]);
        if (absOut > peakOut) peakOut = absOut;
    }

    FLOAT32 inDbfs = peakIn > 0.00001f ? 20.0f * std::log10(peakIn) : -96.0f;
    FLOAT32 outDbfs = peakOut > 0.00001f ? 20.0f * std::log10(peakOut) : -96.0f;

    m_pTelemetry->sampleRate = static_cast<UINT32>(GetFramesPerSecond());
    m_pTelemetry->channelCount = u32Channels;
    m_pTelemetry->processCount++;
    m_pTelemetry->totalFramesProcessed += u32Frames;
    m_pTelemetry->lastPeakInLinear = peakIn;
    m_pTelemetry->lastPeakOutLinear = peakOut;
    m_pTelemetry->lastPeakInDbfs = inDbfs;
    m_pTelemetry->lastPeakOutDbfs = outDbfs;
    m_pTelemetry->lastProcessTick = GetTickCount64();
}

// IUnknown
STDMETHODIMP CPermadBLimiterAPO::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown)
    {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObject))
    {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
    {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    }
    else if (riid == __uuidof(IAudioProcessingObjectRT))
    {
        *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    }
    else if (riid == __uuidof(IAudioSystemEffects))
    {
        *ppv = static_cast<IAudioSystemEffects*>(this);
    }
    else if (riid == __uuidof(IAudioSystemEffects2))
    {
        *ppv = static_cast<IAudioSystemEffects2*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) CPermadBLimiterAPO::AddRef()
{
    return InterlockedIncrement(&m_cRef);
}

STDMETHODIMP_(ULONG) CPermadBLimiterAPO::Release()
{
    ULONG ulRef = InterlockedDecrement(&m_cRef);
    if (ulRef == 0)
    {
        delete this;
    }
    return ulRef;
}

// IAudioProcessingObject
STDMETHODIMP CPermadBLimiterAPO::Initialize(UINT32 cbDataSize, BYTE* pbyData)
{
    if (cbDataSize != 0 && pbyData == nullptr) return E_INVALIDARG;
    if (cbDataSize == 0 && pbyData != nullptr) return E_INVALIDARG;

    m_bIsInitialized = true;
    return S_OK;
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

    UINT32 u32Frames = pIn->u32ValidFrameCount;
    UINT32 u32Channels = GetSamplesPerFrame();

    FLOAT32* pf32In = reinterpret_cast<FLOAT32*>(pIn->pBuffer);
    FLOAT32* pf32Out = reinterpret_cast<FLOAT32*>(pOut->pBuffer);

    if (pIn->u32BufferFlags == BUFFER_SILENT)
    {
        ZeroMemory(pf32Out, sizeof(FLOAT32) * u32Frames * u32Channels);
        pOut->u32ValidFrameCount = u32Frames;
        pOut->u32BufferFlags = BUFFER_SILENT;
        return;
    }

    // Phase 1 Requirement: Apply fixed -12 dB gain
    // Gain factor: 10^(-12 / 20) = 0.25118864f
    const FLOAT32 kGain = 0.25118864f;

    UINT32 totalSamples = u32Frames * u32Channels;
    for (UINT32 i = 0; i < totalSamples; ++i)
    {
        pf32Out[i] = pf32In[i] * kGain;
    }

    pOut->u32ValidFrameCount = u32Frames;
    pOut->u32BufferFlags = BUFFER_VALID;

    // Update real-time telemetry
    UpdateTelemetry(u32Frames, u32Channels, pf32In, pf32Out);
}

// IAudioSystemEffects2
STDMETHODIMP CPermadBLimiterAPO::GetEffectsList(
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

    // Return our custom effect ID
    GUID* pGuid = reinterpret_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
    if (!pGuid)
    {
        *ppEffectsIds = nullptr;
        *pcEffects = 0;
        return E_OUTOFMEMORY;
    }

    *pGuid = PermadBEffectId;
    *ppEffectsIds = pGuid;
    *pcEffects = 1;

    return S_OK;
}

// --------------------------------------------------------------------------
// COM Class Factory Implementation
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
        if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;
        if (!ppv) return E_POINTER;

        CPermadBLimiterAPO* pApo = new (std::nothrow) CPermadBLimiterAPO();
        if (!pApo) return E_OUTOFMEMORY;

        HRESULT hr = pApo->QueryInterface(riid, ppv);
        pApo->Release();
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
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (rclsid == CLSID_PermadBLimiterAPO)
    {
        CPermadBLimiterClassFactory* pFactory = new (std::nothrow) CPermadBLimiterClassFactory();
        if (!pFactory) return E_OUTOFMEMORY;

        HRESULT hr = pFactory->QueryInterface(riid, ppv);
        pFactory->Release();
        return hr;
    }

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

STDAPI DllRegisterServer()
{
    WCHAR szModule[MAX_PATH];
    if (GetModuleFileNameW(g_hModule, szModule, MAX_PATH) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    LPCWSTR clsidStr = L"{968ff234-1895-49f1-8b97-9d9075eb25b6}";
    WCHAR clsidKey[256];
    swprintf_s(clsidKey, L"CLSID\\%s", clsidStr);

    // Register CLSID
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, clsidKey, NULL, L"PermadB Limiter APO");
    WCHAR inprocKey[256];
    swprintf_s(inprocKey, L"CLSID\\%s\\InProcServer32", clsidStr);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, inprocKey, NULL, szModule);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, inprocKey, L"ThreadingModel", L"Both");

    // Register AudioEngine\AudioProcessingObjects entry
    WCHAR apoKey[256];
    swprintf_s(apoKey, L"AudioEngine\\AudioProcessingObjects\\%s", clsidStr);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"FriendlyName", L"PermadB Limiter APO");
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"Copyright", L"PermadB");
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MajorVersion", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinorVersion", 0);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"Flags", 15); // DEFAULT | INPLACE
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinInputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxInputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MinOutputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxOutputConnections", 1);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"MaxInstances", 0xFFFFFFFF);
    SetRegDword(HKEY_CLASSES_ROOT, apoKey, L"NumAPOInterfaces", 1);
    SetRegKeyAndValue(HKEY_CLASSES_ROOT, apoKey, L"APOInterface0", L"{65589647-7974-40AB-9A02-4F0E9777ABC0}");

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    LPCWSTR clsidStr = L"{968ff234-1895-49f1-8b97-9d9075eb25b6}";
    WCHAR clsidKey[256];
    swprintf_s(clsidKey, L"CLSID\\%s", clsidStr);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, clsidKey);

    WCHAR apoKey[256];
    swprintf_s(apoKey, L"AudioEngine\\AudioProcessingObjects\\%s", clsidStr);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, apoKey);

    return S_OK;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}
