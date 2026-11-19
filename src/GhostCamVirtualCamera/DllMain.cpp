#include <windows.h>
#include <string>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>

#include "SharedMemoryFormat.h"
#include "GhostCamMediaSource.h"

static HMODULE g_hModule = NULL;
static volatile LONG g_serverLocks = 0;

ULONG DllAddRef()
{
    return InterlockedIncrement(&g_serverLocks);
}

ULONG DllRelease()
{
    return InterlockedDecrement(&g_serverLocks);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID /*lpReserved*/)
{
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        g_hModule = hModule;
        break;
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

// ============================================================================
// Class Factory for CLSID_GhostCamActivate
// ============================================================================
class GhostCamClassFactory : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    IClassFactory>
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        LogVcamDebug("GhostCamClassFactory::CreateInstance");
        if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;
        if (!ppv) return E_POINTER;
        *ppv = nullptr;

        auto spSource = Microsoft::WRL::Make<GhostCamMediaSource>();
        if (!spSource) return E_OUTOFMEMORY;

        HRESULT hr = spSource->Initialize(nullptr);
        if (FAILED(hr)) {
            LogVcamDebug("GhostCamClassFactory::CreateInstance -> Initialize failed hr=0x%08X", hr);
            return hr;
        }

        hr = spSource->QueryInterface(riid, ppv);
        LogVcamDebug("GhostCamClassFactory::CreateInstance -> QueryInterface hr=0x%08X", hr);
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock) {
            DllAddRef();
        } else {
            DllRelease();
        }
        return S_OK;
    }
};

// ============================================================================
// Exported COM Functions
// ============================================================================
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    LogVcamDebug("DllGetClassObject");
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (InlineIsEqualGUID(rclsid, CLSID_GhostCamActivate)) {
        auto spFactory = Microsoft::WRL::Make<GhostCamClassFactory>();
        if (!spFactory) return E_OUTOFMEMORY;
        return spFactory->QueryInterface(riid, ppv);
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow()
{
    return (g_serverLocks == 0) ? S_OK : S_FALSE;
}

static HRESULT RegisterRegistryValue(HKEY hRoot, const wchar_t* subKey, const wchar_t* valueName, const wchar_t* data)
{
    HKEY hKey = NULL;
    LSTATUS status = RegCreateKeyExW(hRoot, subKey, 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);

    status = RegSetValueExW(
        hKey,
        valueName,
        0,
        REG_SZ,
        reinterpret_cast<const BYTE*>(data),
        static_cast<DWORD>((wcslen(data) + 1) * sizeof(wchar_t)));

    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllRegisterServer()
{
    wchar_t szDllPath[MAX_PATH] = {};
    if (GetModuleFileNameW(g_hModule, szDllPath, MAX_PATH) == 0) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    std::wstring clsidKey = L"SOFTWARE\\Classes\\CLSID\\";
    clsidKey += GHOSTCAM_CLSID_STRING;

    std::wstring inprocKey = clsidKey + L"\\InprocServer32";

    // Attempt HKLM first (required for Windows 11 FrameServer system service)
    HRESULT hr = RegisterRegistryValue(HKEY_LOCAL_MACHINE, clsidKey.c_str(), NULL, GHOSTCAM_FRIENDLY_NAME);
    if (SUCCEEDED(hr)) {
        RegisterRegistryValue(HKEY_LOCAL_MACHINE, inprocKey.c_str(), NULL, szDllPath);
        RegisterRegistryValue(HKEY_LOCAL_MACHINE, inprocKey.c_str(), L"ThreadingModel", L"Both");
    } else {
        // Fallback to HKCU if not elevated
        hr = RegisterRegistryValue(HKEY_CURRENT_USER, clsidKey.c_str(), NULL, GHOSTCAM_FRIENDLY_NAME);
        if (SUCCEEDED(hr)) {
            RegisterRegistryValue(HKEY_CURRENT_USER, inprocKey.c_str(), NULL, szDllPath);
            RegisterRegistryValue(HKEY_CURRENT_USER, inprocKey.c_str(), L"ThreadingModel", L"Both");
        }
    }

    return hr;
}

STDAPI DllUnregisterServer()
{
    std::wstring clsidKey = L"SOFTWARE\\Classes\\CLSID\\";
    clsidKey += GHOSTCAM_CLSID_STRING;

    RegDeleteTreeW(HKEY_LOCAL_MACHINE, clsidKey.c_str());
    RegDeleteTreeW(HKEY_CURRENT_USER, clsidKey.c_str());

    return S_OK;
}

// ============================================================================
// Exported C Functions for Ghostcam Session Management
// ============================================================================
extern "C" HRESULT __stdcall GhostCam_CreateAndStartSession(LPCWSTR friendlyName, void** ppVirtualCamera)
{
    LogVcamDebug("GhostCam_CreateAndStartSession(friendlyName=%S)", friendlyName ? friendlyName : L"null");
    if (!ppVirtualCamera) {
        return E_POINTER;
    }
    *ppVirtualCamera = nullptr;

    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        LogVcamDebug("GhostCam_CreateAndStartSession: MFStartup failed hr=0x%08X", hr);
        return hr;
    }

    Microsoft::WRL::ComPtr<IMFVirtualCamera> spVcam;
    hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_Session,
        MFVirtualCameraAccess_CurrentUser,
        (friendlyName && friendlyName[0] != L'\0') ? friendlyName : L"Ghost Cam",
        L"{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}",
        nullptr,
        0,
        &spVcam);

    if (FAILED(hr)) {
        LogVcamDebug("GhostCam_CreateAndStartSession: MFCreateVirtualCamera failed hr=0x%08X", hr);
        return hr;
    }

    hr = spVcam->Start(nullptr);
    if (FAILED(hr)) {
        LogVcamDebug("GhostCam_CreateAndStartSession: spVcam->Start failed hr=0x%08X", hr);
        spVcam->Shutdown();
        return hr;
    }

    LogVcamDebug("GhostCam_CreateAndStartSession: SUCCESS! Detaching camera pointer");
    *ppVirtualCamera = spVcam.Detach();
    return S_OK;
}

extern "C" HRESULT __stdcall GhostCam_StopAndReleaseSession(void* pVirtualCamera)
{
    LogVcamDebug("GhostCam_StopAndReleaseSession(pVirtualCamera=%p)", pVirtualCamera);
    if (!pVirtualCamera) {
        return S_OK;
    }

    IMFVirtualCamera* pCam = reinterpret_cast<IMFVirtualCamera*>(pVirtualCamera);
    pCam->Stop();
    pCam->Shutdown();
    pCam->Release();

    return S_OK;
}
