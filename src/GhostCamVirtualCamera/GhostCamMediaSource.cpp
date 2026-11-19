#include "GhostCamMediaSource.h"

#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>
#include <ksmedia.h>
#include <shlwapi.h>

#include <cmath>
#include <cstdlib>
#include <cstring>

// Helper to construct standard video media types (YUY2 and NV12)
static HRESULT CreateVideoMediaType(uint32_t width, uint32_t height, uint32_t fps, REFGUID subType, IMFMediaType** ppType)
{
    if (!ppType) return E_POINTER;
    *ppType = nullptr;

    Microsoft::WRL::ComPtr<IMFMediaType> spType;
    HRESULT hr = MFCreateMediaType(&spType);
    if (FAILED(hr)) return hr;

    hr = spType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return hr;

    hr = spType->SetGUID(MF_MT_SUBTYPE, subType);
    if (FAILED(hr)) return hr;

    hr = MFSetAttributeSize(spType.Get(), MF_MT_FRAME_SIZE, width, height);
    if (FAILED(hr)) return hr;

    hr = MFSetAttributeRatio(spType.Get(), MF_MT_FRAME_RATE, fps, 1);
    if (FAILED(hr)) return hr;

    hr = MFSetAttributeRatio(spType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) return hr;

    hr = spType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return hr;

    hr = spType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (FAILED(hr)) return hr;

    bool isNv12 = (subType == MFVideoFormat_NV12);
    UINT32 stride = isNv12 ? width : (width * 2);
    hr = spType->SetUINT32(MF_MT_DEFAULT_STRIDE, stride);
    if (FAILED(hr)) return hr;

    UINT32 sampleSize = isNv12 ? (width * height * 3 / 2) : (width * height * 2);
    hr = spType->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize);
    if (FAILED(hr)) return hr;

    hr = spType->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    if (FAILED(hr)) return hr;

    UINT64 rawBitrate = static_cast<UINT64>(sampleSize) * 8ULL * fps;
    UINT32 avgBitrate = (rawBitrate > UINT32_MAX) ? UINT32_MAX : static_cast<UINT32>(rawBitrate);
    hr = spType->SetUINT32(MF_MT_AVG_BITRATE, avgBitrate);
    if (FAILED(hr)) return hr;

    hr = MFSetAttributeRatio(spType.Get(), MF_MT_FRAME_RATE_RANGE_MIN, fps, 1);
    if (FAILED(hr)) return hr;

    hr = MFSetAttributeRatio(spType.Get(), MF_MT_FRAME_RATE_RANGE_MAX, fps, 1);
    if (FAILED(hr)) return hr;

    *ppType = spType.Detach();
    return S_OK;
}

// ============================================================================
// GhostCamMediaSource (Unified COM object)
// ============================================================================
GhostCamMediaSource::GhostCamMediaSource()
{
    LogVcamDebug("GhostCamMediaSource::GhostCamMediaSource (constructor)");
    MFCreateEventQueue(&m_spEventQueue);
    MFCreateAttributes(&m_spAttributes, 16);
}

GhostCamMediaSource::~GhostCamMediaSource()
{
    LogVcamDebug("GhostCamMediaSource::~GhostCamMediaSource (destructor)");
    if (!m_isShutdown) {
        Shutdown();
    }
}

HRESULT GhostCamMediaSource::Initialize(IMFAttributes* pAttrs)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    return InitializeLocked(pAttrs);
}

HRESULT GhostCamMediaSource::InitializeLocked(IMFAttributes* pAttrs)
{
    if (m_initialized) {
        if (pAttrs && m_spAttributes) {
            pAttrs->CopyAllItems(m_spAttributes.Get());
        }
        return S_OK;
    }

    LogVcamDebug("GhostCamMediaSource::InitializeLocked");

    if (pAttrs && m_spAttributes) {
        pAttrs->CopyAllItems(m_spAttributes.Get());
    }

    // Populate standard Camera Source attributes
    m_spAttributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    m_spAttributes->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, GHOSTCAM_FRIENDLY_NAME);
    m_spAttributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY, KSCATEGORY_VIDEO_CAMERA);
    m_spAttributes->SetUINT32(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_HW_SOURCE, 0);

    // Ensure ProgramData\Ghostcam directory exists
    CreateDirectoryW(L"C:\\ProgramData", NULL);
    CreateDirectoryW(GHOSTCAM_SHM_DIR, NULL);

    SECURITY_ATTRIBUTES sa = {};
    PSECURITY_DESCRIPTOR pSd = nullptr;
    SECURITY_ATTRIBUTES* pSaPtr = nullptr;
    if (CreateGhostCamSecurityAttributes(&sa, &pSd)) {
        pSaPtr = &sa;
    }

    // Open/create SHM file
    m_hShmFile = CreateFileW(
        GHOSTCAM_SHM_FILE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        pSaPtr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (m_hShmFile == INVALID_HANDLE_VALUE) {
        m_hShmFile = CreateFileW(
            GHOSTCAM_SHM_FILE_PATH,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
    }

    if (m_hShmFile == INVALID_HANDLE_VALUE) {
        m_hShmFile = CreateFileW(
            GHOSTCAM_SHM_FILE_PATH,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
    }

    if (m_hShmFile != INVALID_HANDLE_VALUE) {
        DWORD flProtect = PAGE_READWRITE;
        DWORD dwDesiredAccess = FILE_MAP_READ | FILE_MAP_WRITE;

        m_hShmMapping = CreateFileMappingW(
            m_hShmFile,
            pSaPtr,
            flProtect,
            0,
            GHOSTCAM_SHM_SIZE,
            NULL);

        if (!m_hShmMapping) {
            m_hShmMapping = CreateFileMappingW(
                m_hShmFile,
                NULL,
                flProtect,
                0,
                GHOSTCAM_SHM_SIZE,
                NULL);
        }

        if (!m_hShmMapping) {
            flProtect = PAGE_READONLY;
            dwDesiredAccess = FILE_MAP_READ;
            m_hShmMapping = CreateFileMappingW(
                m_hShmFile,
                NULL,
                flProtect,
                0,
                GHOSTCAM_SHM_SIZE,
                NULL);
        }

        if (m_hShmMapping) {
            m_pShmView = MapViewOfFile(
                m_hShmMapping,
                dwDesiredAccess,
                0,
                0,
                GHOSTCAM_SHM_SIZE);

            if (m_pShmView) {
                m_pShmHeader = reinterpret_cast<GhostCamShmHeader*>(m_pShmView);
                if (flProtect == PAGE_READWRITE && m_pShmHeader->Magic != GHOSTCAM_MAGIC_GHST) {
                    m_pShmHeader->Magic = GHOSTCAM_MAGIC_GHST;
                    m_pShmHeader->Version = GHOSTCAM_VERSION_1;
                    m_pShmHeader->FrameSeq = 0;
                    m_pShmHeader->Width = 1280;
                    m_pShmHeader->Height = 720;
                    m_pShmHeader->Fps = 30;
                    m_pShmHeader->Format = GHOSTCAM_FORMAT_YUY2;
                    m_pShmHeader->PayloadBytes = 1280 * 720 * 2;
                    m_pShmHeader->TimestampTicks = 0;
                    m_pShmHeader->Flags = 0; // Idle
                    memset(m_pShmHeader->Reserved, 0, sizeof(m_pShmHeader->Reserved));
                }
            }
        }
    }

    // Open/create sync event Global\GhostCam_FrameSync
    m_hSyncEvent = CreateEventW(pSaPtr, FALSE, FALSE, GHOSTCAM_EVENT_NAME);
    if (!m_hSyncEvent) {
        m_hSyncEvent = CreateEventW(NULL, FALSE, FALSE, GHOSTCAM_EVENT_NAME);
    }
    if (!m_hSyncEvent) {
        m_hSyncEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, GHOSTCAM_EVENT_NAME);
    }
    if (!m_hSyncEvent) {
        m_hSyncEvent = OpenEventW(SYNCHRONIZE, FALSE, GHOSTCAM_EVENT_NAME);
    }

    FreeGhostCamSecurityAttributes(pSd);

    // Register 12 Media Types: 6 YUY2 + 6 NV12
    struct FormatDesc {
        uint32_t width;
        uint32_t height;
        uint32_t fps;
        GUID subType;
    };
    const FormatDesc formats[12] = {
        // YUY2
        { 1280, 720,  60, MFVideoFormat_YUY2 },
        { 1280, 720,  30, MFVideoFormat_YUY2 },
        { 1920, 1080, 60, MFVideoFormat_YUY2 },
        { 1920, 1080, 30, MFVideoFormat_YUY2 },
        { 3840, 2160, 60, MFVideoFormat_YUY2 },
        { 3840, 2160, 30, MFVideoFormat_YUY2 },
        // NV12
        { 1280, 720,  60, MFVideoFormat_NV12 },
        { 1280, 720,  30, MFVideoFormat_NV12 },
        { 1920, 1080, 60, MFVideoFormat_NV12 },
        { 1920, 1080, 30, MFVideoFormat_NV12 },
        { 3840, 2160, 60, MFVideoFormat_NV12 },
        { 3840, 2160, 30, MFVideoFormat_NV12 },
    };

    Microsoft::WRL::ComPtr<IMFMediaType> spMediaTypes[12];
    IMFMediaType* mediaTypes[12] = {};
    for (int i = 0; i < 12; ++i) {
        HRESULT hrType = CreateVideoMediaType(formats[i].width, formats[i].height, formats[i].fps, formats[i].subType, &spMediaTypes[i]);
        if (FAILED(hrType)) return hrType;
        mediaTypes[i] = spMediaTypes[i].Get();
    }

    HRESULT hr = MFCreateStreamDescriptor(0, 12, mediaTypes, &m_spStreamDesc);
    if (FAILED(hr)) return hr;

    Microsoft::WRL::ComPtr<IMFMediaTypeHandler> spHandler;
    hr = m_spStreamDesc->GetMediaTypeHandler(&spHandler);
    if (SUCCEEDED(hr)) {
        spHandler->SetCurrentMediaType(mediaTypes[0]); // 1280x720 @ 60 FPS YUY2
    }

    // Windows 11 FrameServer mandatory attributes
    m_spStreamDesc->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE);
    m_spStreamDesc->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    m_spStreamDesc->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    m_spStreamDesc->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);

    // Create Presentation Descriptor
    IMFStreamDescriptor* pStreamDescs[] = { m_spStreamDesc.Get() };
    hr = MFCreatePresentationDescriptor(1, pStreamDescs, &m_spPresDesc);
    if (FAILED(hr)) return hr;

    hr = m_spPresDesc->SelectStream(0);
    if (FAILED(hr)) return hr;

    // Instantiate and initialize the stream
    m_spStream = Microsoft::WRL::Make<GhostCamMediaStream>();
    if (!m_spStream) return E_OUTOFMEMORY;

    hr = m_spStream->Initialize(this, m_spStreamDesc.Get());
    if (FAILED(hr)) return hr;

    m_initialized = true;
    LogVcamDebug("GhostCamMediaSource::InitializeLocked -> SUCCESS");
    return S_OK;
}

// ============================================================================
// IMFActivate
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::ActivateObject(REFIID riid, void** ppv)
{
    LogVcamDebug("GhostCamMediaSource::ActivateObject");
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (!m_initialized) {
        HRESULT hr = InitializeLocked(m_spAttributes.Get());
        if (FAILED(hr)) return hr;
    }

    return this->QueryInterface(riid, ppv);
}

IFACEMETHODIMP GhostCamMediaSource::ShutdownObject()
{
    LogVcamDebug("GhostCamMediaSource::ShutdownObject (no-op to prevent premature teardown)");
    // Deliberate no-op: do not kill the source when FrameServer releases IMFActivate!
    return S_OK;
}

IFACEMETHODIMP GhostCamMediaSource::DetachObject()
{
    LogVcamDebug("GhostCamMediaSource::DetachObject");
    return S_OK;
}

// ============================================================================
// IMFAttributes implementation (delegates to m_spAttributes)
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::GetItem(REFGUID guidKey, PROPVARIANT* pValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetItem(guidKey, pValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetItemType(REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetItemType(guidKey, pType) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::CompareItem(REFGUID guidKey, REFPROPVARIANT Value, BOOL* pbResult) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->CompareItem(guidKey, Value, pbResult) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::Compare(IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE MatchType, BOOL* pbResult) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->Compare(pTheirs, MatchType, pbResult) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetUINT32(REFGUID guidKey, UINT32* punValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetUINT32(guidKey, punValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetUINT64(REFGUID guidKey, UINT64* punValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetUINT64(guidKey, punValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetDouble(REFGUID guidKey, double* pfValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetDouble(guidKey, pfValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetGUID(REFGUID guidKey, GUID* pguidValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetGUID(guidKey, pguidValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetStringLength(REFGUID guidKey, UINT32* pcchLength) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetStringLength(guidKey, pcchLength) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetString(REFGUID guidKey, LPWSTR pwszValue, UINT32 cchBufSize, UINT32* pcchLength) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetString(guidKey, pwszValue, cchBufSize, pcchLength) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetAllocatedString(REFGUID guidKey, LPWSTR* ppwszValue, UINT32* pcchLength) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetAllocatedString(guidKey, ppwszValue, pcchLength) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetBlobSize(REFGUID guidKey, UINT32* pcbBlobSize) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetBlobSize(guidKey, pcbBlobSize) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetBlob(REFGUID guidKey, UINT8* pBuf, UINT32 cbBufSize, UINT32* pcbBlobSize) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetBlob(guidKey, pBuf, cbBufSize, pcbBlobSize) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetAllocatedBlob(REFGUID guidKey, UINT8** ppBuf, UINT32* pcbSize) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetAllocatedBlob(guidKey, ppBuf, pcbSize) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetUnknown(REFGUID guidKey, REFIID riid, LPVOID* ppv) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetUnknown(guidKey, riid, ppv) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetItem(REFGUID guidKey, REFPROPVARIANT Value) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetItem(guidKey, Value) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::DeleteItem(REFGUID guidKey) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->DeleteItem(guidKey) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::DeleteAllItems() {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->DeleteAllItems() : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetUINT32(REFGUID guidKey, UINT32 unValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetUINT32(guidKey, unValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetUINT64(REFGUID guidKey, UINT64 unValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetUINT64(guidKey, unValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetDouble(REFGUID guidKey, double fValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetDouble(guidKey, fValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetGUID(REFGUID guidKey, REFGUID guidValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetGUID(guidKey, guidValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetString(REFGUID guidKey, LPCWSTR wszValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetString(guidKey, wszValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetBlob(REFGUID guidKey, const UINT8* pBuf, UINT32 cbBufSize) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetBlob(guidKey, pBuf, cbBufSize) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::SetUnknown(REFGUID guidKey, IUnknown* pUnknown) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->SetUnknown(guidKey, pUnknown) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::LockStore() {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->LockStore() : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::UnlockStore() {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->UnlockStore() : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetCount(UINT32* pcItems) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetCount(pcItems) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::GetItemByIndex(UINT32 unIndex, GUID* pguidKey, PROPVARIANT* pValue) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->GetItemByIndex(unIndex, pguidKey, pValue) : E_UNEXPECTED;
}
IFACEMETHODIMP GhostCamMediaSource::CopyAllItems(IMFAttributes* pDest) {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_spAttributes ? m_spAttributes->CopyAllItems(pDest) : E_UNEXPECTED;
}

// ============================================================================
// IMFMediaSource
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::GetCharacteristics(DWORD* pdwCharacteristics)
{
    LogVcamDebug("GhostCamMediaSource::GetCharacteristics");
    if (!pdwCharacteristics) return E_POINTER;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP GhostCamMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor)
{
    LogVcamDebug("GhostCamMediaSource::CreatePresentationDescriptor");
    if (!ppPresentationDescriptor) return E_POINTER;
    *ppPresentationDescriptor = nullptr;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) {
        LogVcamDebug("GhostCamMediaSource::CreatePresentationDescriptor -> MF_E_SHUTDOWN");
        return MF_E_SHUTDOWN;
    }
    if (!m_initialized) {
        HRESULT hr = InitializeLocked(m_spAttributes.Get());
        if (FAILED(hr)) return hr;
    }
    if (!m_spPresDesc) {
        LogVcamDebug("GhostCamMediaSource::CreatePresentationDescriptor -> MF_E_NOT_INITIALIZED");
        return MF_E_NOT_INITIALIZED;
    }

    HRESULT hr = m_spPresDesc->Clone(ppPresentationDescriptor);
    LogVcamDebug("GhostCamMediaSource::CreatePresentationDescriptor -> Clone hr=0x%08X", hr);
    return hr;
}

IFACEMETHODIMP GhostCamMediaSource::Start(
    IMFPresentationDescriptor* pPresentationDescriptor,
    const GUID* pguidTimeFormat,
    const PROPVARIANT* pvarStartPosition)
{
    LogVcamDebug("GhostCamMediaSource::Start");
    if (!pPresentationDescriptor) return E_POINTER;
    if (pguidTimeFormat && *pguidTimeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;

    BOOL fSelected = FALSE;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> spSD;
    HRESULT hr = pPresentationDescriptor->GetStreamDescriptorByIndex(0, &fSelected, &spSD);
    if (FAILED(hr)) return hr;

    PROPVARIANT varStart;
    PropVariantInit(&varStart);
    if (pvarStartPosition && pvarStartPosition->vt != VT_EMPTY) {
        PropVariantCopy(&varStart, pvarStartPosition);
    } else {
        varStart.vt = VT_I8;
        varStart.hVal.QuadPart = MFGetSystemTime();
    }

    if (fSelected) {
        Microsoft::WRL::ComPtr<IMFMediaTypeHandler> spHandler;
        hr = spSD->GetMediaTypeHandler(&spHandler);
        Microsoft::WRL::ComPtr<IMFMediaType> spMediaType;
        if (SUCCEEDED(hr)) {
            hr = spHandler->GetCurrentMediaType(&spMediaType);
        }

        // Initialize sample allocator if registered
        if (m_spAllocator && spMediaType) {
            if (m_allocatorInitialized) {
                m_spAllocator->UninitializeSampleAllocator();
                m_allocatorInitialized = false;
            }
            HRESULT hrInitAlloc = m_spAllocator->InitializeSampleAllocator(10, spMediaType.Get());
            LogVcamDebug("GhostCamMediaSource::Start -> InitializeSampleAllocator hr=0x%08X", hrInitAlloc);
            if (SUCCEEDED(hrInitAlloc)) {
                m_allocatorInitialized = true;
            }
        }

        if (m_spStream) {
            hr = m_spStream->Start(spMediaType.Get(), &varStart);
            if (FAILED(hr)) {
                PropVariantClear(&varStart);
                return hr;
            }
        }

        MediaEventType streamEvent = m_streamCreatedEventSent ? MEUpdatedStream : MENewStream;
        m_streamCreatedEventSent = true;

        PROPVARIANT varStream;
        PropVariantInit(&varStream);
        varStream.vt = VT_UNKNOWN;
        varStream.punkVal = m_spStream.Get();
        varStream.punkVal->AddRef();
        m_spEventQueue->QueueEventParamVar(streamEvent, GUID_NULL, S_OK, &varStream);
        PropVariantClear(&varStream);
    }

    m_spEventQueue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &varStart);
    PropVariantClear(&varStart);

    LogVcamDebug("GhostCamMediaSource::Start -> S_OK");
    return S_OK;
}

IFACEMETHODIMP GhostCamMediaSource::Stop()
{
    LogVcamDebug("GhostCamMediaSource::Stop");
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;

    if (m_spStream) {
        m_spStream->Stop();
    }

    if (m_spAllocator && m_allocatorInitialized) {
        m_spAllocator->UninitializeSampleAllocator();
        m_allocatorInitialized = false;
    }

    PROPVARIANT varStop;
    PropVariantInit(&varStop);
    m_spEventQueue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &varStop);
    PropVariantClear(&varStop);

    return S_OK;
}

IFACEMETHODIMP GhostCamMediaSource::Pause()
{
    LogVcamDebug("GhostCamMediaSource::Pause");
    return MF_E_INVALID_STATE_TRANSITION;
}

IFACEMETHODIMP GhostCamMediaSource::Shutdown()
{
    LogVcamDebug("GhostCamMediaSource::Shutdown");
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    m_isShutdown = true;
    m_allocatorInitialized = false;
    m_streamCreatedEventSent = false;

    if (m_spStream) {
        m_spStream->Shutdown();
        m_spStream.Reset();
    }
    if (m_spEventQueue) {
        m_spEventQueue->Shutdown();
        m_spEventQueue.Reset();
    }
    if (m_spAllocator) {
        m_spAllocator->UninitializeSampleAllocator();
        m_spAllocator.Reset();
    }
    m_spD3DManager.Reset();
    m_spPresDesc.Reset();
    m_spStreamDesc.Reset();

    if (m_pShmView) {
        UnmapViewOfFile(m_pShmView);
        m_pShmView = nullptr;
        m_pShmHeader = nullptr;
    }
    if (m_hShmMapping) {
        CloseHandle(m_hShmMapping);
        m_hShmMapping = NULL;
    }
    if (m_hShmFile != INVALID_HANDLE_VALUE) {
        CloseHandle(m_hShmFile);
        m_hShmFile = INVALID_HANDLE_VALUE;
    }
    if (m_hSyncEvent) {
        CloseHandle(m_hSyncEvent);
        m_hSyncEvent = NULL;
    }
    return S_OK;
}

// ============================================================================
// IMFMediaSourceEx
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::GetSourceAttributes(IMFAttributes** ppAttributes)
{
    LogVcamDebug("GhostCamMediaSource::GetSourceAttributes");
    if (!ppAttributes) return E_POINTER;
    *ppAttributes = nullptr;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    return m_spAttributes.CopyTo(ppAttributes);
}

IFACEMETHODIMP GhostCamMediaSource::GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes)
{
    LogVcamDebug("GhostCamMediaSource::GetStreamAttributes(stream=%lu)", dwStreamIdentifier);
    if (dwStreamIdentifier != 0) return MF_E_INVALIDSTREAMNUMBER;
    if (!ppAttributes) return E_POINTER;
    *ppAttributes = nullptr;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    if (m_spStream) {
        return m_spStream->GetStreamAttributes(ppAttributes);
    }
    return m_spStreamDesc.CopyTo(ppAttributes);
}

IFACEMETHODIMP GhostCamMediaSource::SetD3DManager(IUnknown* pManager)
{
    LogVcamDebug("GhostCamMediaSource::SetD3DManager(pManager=%p)", pManager);
    std::lock_guard<std::mutex> lock(m_mutex);
    m_spD3DManager = pManager;
    if (m_spAllocator) {
        m_spAllocator->SetDirectXManager(m_spD3DManager.Get());
    }
    return S_OK;
}

// ============================================================================
// IMFMediaEventGenerator
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    return m_spEventQueue ? m_spEventQueue->GetEvent(dwFlags, ppEvent) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaSource::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    return m_spEventQueue ? m_spEventQueue->BeginGetEvent(pCallback, punkState) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaSource::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    return m_spEventQueue ? m_spEventQueue->EndGetEvent(pResult, ppEvent) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaSource::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    return m_spEventQueue ? m_spEventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue) : MF_E_SHUTDOWN;
}

// ============================================================================
// IMFGetService
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::GetService(REFGUID guidService, REFIID /*riid*/, LPVOID* ppvObject)
{
    OLECHAR szSvc[64] = {};
    StringFromGUID2(guidService, szSvc, 64);
    LogVcamDebug("GhostCamMediaSource::GetService(svc=%S)", szSvc);
    if (!ppvObject) return E_POINTER;
    *ppvObject = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

// ============================================================================
// IKsControl
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::KsProperty(PKSPROPERTY /*Property*/, ULONG /*PropertyLength*/, LPVOID /*PropertyData*/, ULONG /*DataLength*/, ULONG* BytesReturned)
{
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
IFACEMETHODIMP GhostCamMediaSource::KsMethod(PKSMETHOD /*Method*/, ULONG /*MethodLength*/, LPVOID /*MethodData*/, ULONG /*DataLength*/, ULONG* BytesReturned)
{
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
IFACEMETHODIMP GhostCamMediaSource::KsEvent(PKSEVENT /*Event*/, ULONG /*EventLength*/, LPVOID /*EventData*/, ULONG /*DataLength*/, ULONG* BytesReturned)
{
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// ============================================================================
// IMFSampleAllocatorControl
// ============================================================================
IFACEMETHODIMP GhostCamMediaSource::SetDefaultAllocator(DWORD dwOutputStreamID, IUnknown* pAllocator)
{
    LogVcamDebug("GhostCamMediaSource::SetDefaultAllocator(stream=%lu, pAllocator=%p)", dwOutputStreamID, pAllocator);
    if (dwOutputStreamID != 0) return MF_E_INVALIDSTREAMNUMBER;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_spAllocator && m_allocatorInitialized) {
        m_spAllocator->UninitializeSampleAllocator();
        m_allocatorInitialized = false;
    }
    m_spAllocator.Reset();
    if (pAllocator) {
        HRESULT hrAlloc = pAllocator->QueryInterface(IID_PPV_ARGS(&m_spAllocator));
        LogVcamDebug("GhostCamMediaSource::SetDefaultAllocator -> QI(IMFVideoSampleAllocator) hr=0x%08X", hrAlloc);
        if (m_spAllocator && m_spD3DManager) {
            HRESULT hrD3D = m_spAllocator->SetDirectXManager(m_spD3DManager.Get());
            LogVcamDebug("GhostCamMediaSource::SetDefaultAllocator -> SetDirectXManager hr=0x%08X", hrD3D);
        }
    }
    return S_OK;
}

IFACEMETHODIMP GhostCamMediaSource::GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID, MFSampleAllocatorUsage* peUsage)
{
    LogVcamDebug("GhostCamMediaSource::GetAllocatorUsage(stream=%lu)", dwOutputStreamID);
    if (dwOutputStreamID != 0) return MF_E_INVALIDSTREAMNUMBER;
    if (!peUsage) return E_POINTER;
    if (pdwInputStreamID) *pdwInputStreamID = dwOutputStreamID;

    std::lock_guard<std::mutex> lock(m_mutex);
    // Crucial: FrameServer expects UsesProvidedAllocator so it supplies the cross-process buffer allocator
    *peUsage = MFSampleAllocatorUsage_UsesProvidedAllocator;
    return S_OK;
}

// ============================================================================
// GhostCamMediaStream
// ============================================================================
GhostCamMediaStream::GhostCamMediaStream()
{
}

GhostCamMediaStream::~GhostCamMediaStream()
{
    Shutdown();
}

HRESULT GhostCamMediaStream::Initialize(GhostCamMediaSource* pSource, IMFStreamDescriptor* pStreamDesc)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_pSource = pSource;
    m_spStreamDesc = pStreamDesc;

    HRESULT hr = MFCreateEventQueue(&m_spEventQueue);
    if (FAILED(hr)) return hr;

    hr = MFCreateAttributes(&m_spStreamAttributes, 8);
    if (FAILED(hr)) return hr;

    if (m_spStreamDesc) {
        m_spStreamDesc->CopyAllItems(m_spStreamAttributes.Get());
    }

    m_hRequestEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
    m_hStopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);

    m_hWaitTimer = CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!m_hWaitTimer) {
        m_hWaitTimer = CreateWaitableTimerW(NULL, FALSE, NULL);
    }

    return S_OK;
}

HRESULT GhostCamMediaStream::Start(IMFMediaType* pMediaType, const PROPVARIANT* pvarStartPosition)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;

    if (pMediaType) {
        m_spCurrentMediaType = pMediaType;

        GUID subType = GUID_NULL;
        if (SUCCEEDED(pMediaType->GetGUID(MF_MT_SUBTYPE, &subType))) {
            m_subType = subType;
        }

        UINT32 w = 0, h = 0;
        if (SUCCEEDED(MFGetAttributeSize(pMediaType, MF_MT_FRAME_SIZE, &w, &h)) && w > 0 && h > 0) {
            m_width = w;
            m_height = h;
        }

        UINT32 num = 0, den = 0;
        if (SUCCEEDED(MFGetAttributeRatio(pMediaType, MF_MT_FRAME_RATE, &num, &den)) && num > 0 && den > 0) {
            m_frameDuration = static_cast<LONGLONG>((10000000ULL * den) / num);
        }

        if (m_spStreamDesc) {
            Microsoft::WRL::ComPtr<IMFMediaTypeHandler> spHandler;
            if (SUCCEEDED(m_spStreamDesc->GetMediaTypeHandler(&spHandler)) && spHandler) {
                spHandler->SetCurrentMediaType(pMediaType);
            }
        }
    }

    m_lastShmSeq = -1;
    m_lastShmW = 0;
    m_lastShmH = 0;

    m_sampleTime = 0;
    m_lastSampleTime = 0;
    m_samplesDelivered = 0;
    m_allocFailureCount = 0;
    m_streamState = MF_STREAM_STATE_RUNNING;
    m_isStreaming = true;

    ResetEvent(m_hStopEvent);

    if (!m_workerThread.joinable()) {
        m_workerThread = std::thread(&GhostCamMediaStream::WorkerThreadProc, this);
    }

    PROPVARIANT varStart;
    PropVariantInit(&varStart);
    if (pvarStartPosition && pvarStartPosition->vt != VT_EMPTY) {
        PropVariantCopy(&varStart, pvarStartPosition);
    } else {
        varStart.vt = VT_I8;
        varStart.hVal.QuadPart = MFGetSystemTime();
    }
    m_spEventQueue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, &varStart);
    PropVariantClear(&varStart);

    return S_OK;
}

HRESULT GhostCamMediaStream::Stop()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;

    m_isStreaming = false;
    m_streamState = MF_STREAM_STATE_STOPPED;
    SetEvent(m_hStopEvent);

    if (m_workerThread.joinable()) {
        m_workerThread.join();
    }

    {
        std::lock_guard<std::mutex> qLock(m_queueMutex);
        std::queue<Microsoft::WRL::ComPtr<IUnknown>> empty;
        std::swap(m_tokenQueue, empty);
    }

    PROPVARIANT varStop;
    PropVariantInit(&varStop);
    m_spEventQueue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, &varStop);
    PropVariantClear(&varStop);

    return S_OK;
}

HRESULT GhostCamMediaStream::Pause()
{
    return MF_E_INVALID_STATE_TRANSITION;
}

HRESULT GhostCamMediaStream::Shutdown()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    m_isShutdown = true;
    m_isStreaming = false;

    SetEvent(m_hStopEvent);
    if (m_workerThread.joinable()) {
        m_workerThread.join();
    }

    if (m_hWaitTimer) {
        CloseHandle(m_hWaitTimer);
        m_hWaitTimer = NULL;
    }
    if (m_hStopEvent) {
        CloseHandle(m_hStopEvent);
        m_hStopEvent = NULL;
    }
    if (m_hRequestEvent) {
        CloseHandle(m_hRequestEvent);
        m_hRequestEvent = NULL;
    }

    if (m_spEventQueue) {
        m_spEventQueue->Shutdown();
        m_spEventQueue.Reset();
    }

    m_spStreamDesc.Reset();
    m_spStreamAttributes.Reset();
    m_spCurrentMediaType.Reset();
    m_pSource = nullptr;

    return S_OK;
}

HRESULT GhostCamMediaStream::GetStreamAttributes(IMFAttributes** ppAttributes)
{
    if (!ppAttributes) return E_POINTER;
    *ppAttributes = nullptr;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;
    return m_spStreamAttributes.CopyTo(ppAttributes);
}

// IMFMediaStream2
IFACEMETHODIMP GhostCamMediaStream::SetStreamState(MF_STREAM_STATE state)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_streamState = state;
    return S_OK;
}

IFACEMETHODIMP GhostCamMediaStream::GetStreamState(MF_STREAM_STATE* pState)
{
    if (!pState) return E_POINTER;
    std::lock_guard<std::mutex> lock(m_mutex);
    *pState = m_streamState;
    return S_OK;
}

// IMFMediaStream
IFACEMETHODIMP GhostCamMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
    if (!ppMediaSource) return E_POINTER;
    *ppMediaSource = nullptr;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown || !m_pSource) return MF_E_SHUTDOWN;
    return m_pSource->QueryInterface(IID_PPV_ARGS(ppMediaSource));
}

IFACEMETHODIMP GhostCamMediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
    if (!ppStreamDescriptor) return E_POINTER;
    *ppStreamDescriptor = nullptr;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown || !m_spStreamDesc) return MF_E_SHUTDOWN;
    return m_spStreamDesc.CopyTo(ppStreamDescriptor);
}

IFACEMETHODIMP GhostCamMediaStream::RequestSample(IUnknown* pToken)
{
    LogVcamDebug("GhostCamMediaStream::RequestSample(token=%p)", pToken);
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_isShutdown) return MF_E_SHUTDOWN;

    {
        std::lock_guard<std::mutex> qLock(m_queueMutex);
        m_tokenQueue.push(pToken);
    }
    SetEvent(m_hRequestEvent);
    return S_OK;
}

// IMFMediaEventGenerator
IFACEMETHODIMP GhostCamMediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    return m_spEventQueue ? m_spEventQueue->GetEvent(dwFlags, ppEvent) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    return m_spEventQueue ? m_spEventQueue->BeginGetEvent(pCallback, punkState) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    return m_spEventQueue ? m_spEventQueue->EndGetEvent(pResult, ppEvent) : MF_E_SHUTDOWN;
}
IFACEMETHODIMP GhostCamMediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    return m_spEventQueue ? m_spEventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue) : MF_E_SHUTDOWN;
}

// Asynchronous sample delivery worker thread
void GhostCamMediaStream::WorkerThreadProc()
{
    auto lastFrameTime = std::chrono::steady_clock::now();

    while (m_isStreaming && !m_isShutdown) {
        HANDLE waitHandles[] = { m_hStopEvent, m_hRequestEvent };
        DWORD dwWait = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);
        if (dwWait == WAIT_OBJECT_0) {
            break; // Stop signaled
        }
        if (dwWait != WAIT_OBJECT_0 + 1) {
            continue;
        }

        Microsoft::WRL::ComPtr<IUnknown> spToken;
        {
            std::lock_guard<std::mutex> qLock(m_queueMutex);
            if (m_tokenQueue.empty()) {
                ResetEvent(m_hRequestEvent);
                continue;
            }
            spToken = m_tokenQueue.front();
            m_tokenQueue.pop();
            if (!m_tokenQueue.empty()) {
                SetEvent(m_hRequestEvent);
            } else {
                ResetEvent(m_hRequestEvent);
            }
        }

        // 100ns precision frame pacing using waitable timer
        auto now = std::chrono::steady_clock::now();
        LONGLONG elapsed100ns = std::chrono::duration_cast<std::chrono::nanoseconds>(now - lastFrameTime).count() / 100;
        LONGLONG target100ns = (m_frameDuration >= 50000) ? m_frameDuration : 166666;
        HANDLE hSync = m_pSource ? m_pSource->GetSyncEvent() : NULL;

        if (elapsed100ns < target100ns) {
            LONGLONG remaining100ns = target100ns - elapsed100ns;
            if (m_hWaitTimer) {
                LARGE_INTEGER dueTime;
                dueTime.QuadPart = -remaining100ns;
                SetWaitableTimer(m_hWaitTimer, &dueTime, 0, NULL, NULL, FALSE);
                if (hSync) {
                    HANDLE syncHandles[] = { m_hStopEvent, hSync, m_hWaitTimer };
                    DWORD dwSync = WaitForMultipleObjects(3, syncHandles, FALSE, 50);
                    if (dwSync == WAIT_OBJECT_0) break;
                } else {
                    HANDLE timerHandles[] = { m_hStopEvent, m_hWaitTimer };
                    DWORD dwStop = WaitForMultipleObjects(2, timerHandles, FALSE, 50);
                    if (dwStop == WAIT_OBJECT_0) break;
                }
            } else {
                DWORD remainingWait = static_cast<DWORD>((remaining100ns + 9999) / 10000);
                if (hSync) {
                    HANDLE syncHandles[] = { m_hStopEvent, hSync };
                    if (WaitForMultipleObjects(2, syncHandles, FALSE, remainingWait) == WAIT_OBJECT_0) break;
                } else {
                    if (WaitForSingleObject(m_hStopEvent, remainingWait) == WAIT_OBJECT_0) break;
                }
            }
        } else {
            if (WaitForSingleObject(m_hStopEvent, 0) == WAIT_OBJECT_0) break;
        }

        lastFrameTime = std::chrono::steady_clock::now();

        if (!m_isStreaming || m_isShutdown) break;

        ProcessOneSample(spToken.Get());
    }
}

void GhostCamMediaStream::ProcessOneSample(IUnknown* pToken)
{
    uint32_t dstW = m_width;
    uint32_t dstH = m_height;
    uint32_t yuy2Bytes = dstW * dstH * 2;
    bool isNv12 = (m_subType == MFVideoFormat_NV12);
    uint32_t sampleBytes = isNv12 ? (dstW * dstH * 3 / 2) : yuy2Bytes;

    if (m_frameBuffer.size() != yuy2Bytes) {
        m_frameBuffer.resize(yuy2Bytes);
        m_lastShmSeq = -1;
    }

    GhostCamShmHeader* pHeader = m_pSource ? m_pSource->GetShmHeader() : nullptr;
    bool isShmActive = false;

    if (pHeader &&
        pHeader->Magic == GHOSTCAM_MAGIC_GHST &&
        (pHeader->Flags & GHOSTCAM_FLAG_ACTIVE) != 0 &&
        pHeader->Width > 0 && pHeader->Width <= 3840 &&
        pHeader->Height > 0 && pHeader->Height <= 2160 &&
        (sizeof(GhostCamShmHeader) + pHeader->PayloadBytes) <= GHOSTCAM_SHM_SIZE &&
        (static_cast<uint64_t>(pHeader->Width) * pHeader->Height * 2 <= pHeader->PayloadBytes))
    {
        isShmActive = true;
        LONG seq1 = InterlockedCompareExchange(&pHeader->FrameSeq, 0, 0);
        if ((seq1 & 1) == 0) {
            if (seq1 != m_lastShmSeq || pHeader->Width != m_lastShmW || pHeader->Height != m_lastShmH) {
                const uint8_t* pSrcPixels = reinterpret_cast<const uint8_t*>(pHeader) + sizeof(GhostCamShmHeader);
                if (pHeader->Width == dstW && pHeader->Height == dstH) {
                    memcpy(m_frameBuffer.data(), pSrcPixels, yuy2Bytes);
                } else {
                    ScaleYUY2(pSrcPixels, pHeader->Width, pHeader->Height, m_frameBuffer.data(), dstW, dstH);
                }
                LONG seq2 = InterlockedCompareExchange(&pHeader->FrameSeq, 0, 0);
                if (seq1 == seq2) {
                    m_lastShmSeq = seq1;
                    m_lastShmW = pHeader->Width;
                    m_lastShmH = pHeader->Height;
                }
            }
        }
        if (m_lastShmSeq < 0) {
            GenerateStandbyFrame(m_frameBuffer.data(), dstW, dstH, m_standbyFrameIndex++);
        }
    } else {
        m_lastShmSeq = -1;
        m_lastShmW = 0;
        m_lastShmH = 0;
        GenerateStandbyFrame(m_frameBuffer.data(), dstW, dstH, m_standbyFrameIndex++);
    }

    // Allocate sample
    Microsoft::WRL::ComPtr<IMFSample> spSample;
    IMFVideoSampleAllocator* pAlloc = m_pSource ? m_pSource->GetAllocator() : nullptr;

    if (pAlloc) {
        HRESULT hrAlloc = E_FAIL;
        for (int attempt = 0; attempt < 5; ++attempt) {
            hrAlloc = pAlloc->AllocateSample(&spSample);
            if (SUCCEEDED(hrAlloc) && spSample) {
                break;
            }
            if (hrAlloc == MF_E_SAMPLEALLOCATOR_EMPTY && m_isStreaming && !m_isShutdown) {
                WaitForSingleObject(m_hStopEvent, 4);
                continue;
            }
            break;
        }

        if (FAILED(hrAlloc) || !spSample) {
            m_allocFailureCount++;
            if (m_allocFailureCount <= 5 || (m_allocFailureCount % 60) == 0) {
                LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> AllocateSample failed hr=0x%08X (failCount=%u)", hrAlloc, m_allocFailureCount);
            }
            return;
        }
    } else {
        // Fallback allocation when no external allocator was provided
        HRESULT hrSample = MFCreateSample(&spSample);
        if (FAILED(hrSample) || !spSample) {
            LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> MFCreateSample failed hr=0x%08X", hrSample);
            return;
        }
        Microsoft::WRL::ComPtr<IMFMediaBuffer> spBuffer;
        HRESULT hrBuf = MFCreateMemoryBuffer(sampleBytes, &spBuffer);
        if (FAILED(hrBuf) || !spBuffer) {
            LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> MFCreateMemoryBuffer failed hr=0x%08X", hrBuf);
            return;
        }
        spSample->AddBuffer(spBuffer.Get());
    }

    Microsoft::WRL::ComPtr<IMFMediaBuffer> spBuffer;
    HRESULT hrBuf = spSample->GetBufferByIndex(0, &spBuffer);
    if (FAILED(hrBuf) || !spBuffer) {
        LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> GetBufferByIndex(0) failed hr=0x%08X", hrBuf);
        return;
    }

    bool copied = false;
    Microsoft::WRL::ComPtr<IMF2DBuffer2> sp2D2;
    if (SUCCEEDED(spBuffer.As(&sp2D2))) {
        BYTE* pScanline0 = nullptr;
        LONG lPitch = 0;
        BYTE* pBufferStart = nullptr;
        DWORD cbBuffer = 0;
        HRESULT hrLock = sp2D2->Lock2DSize(MF2DBuffer_LockFlags_Write, &pScanline0, &lPitch, &pBufferStart, &cbBuffer);
        if (SUCCEEDED(hrLock) && pScanline0) {
            if (!isNv12) {
                uint32_t srcStride = dstW * 2;
                if (lPitch == static_cast<LONG>(srcStride)) {
                    memcpy(pScanline0, m_frameBuffer.data(), sampleBytes);
                } else if (std::abs(lPitch) >= static_cast<LONG>(srcStride)) {
                    for (uint32_t y = 0; y < dstH; ++y) {
                        memcpy(pScanline0 + static_cast<ptrdiff_t>(y) * lPitch,
                               m_frameBuffer.data() + static_cast<size_t>(y) * srcStride,
                               srcStride);
                    }
                }
            } else {
                if (lPitch >= static_cast<LONG>(dstW) && dstH >= 2) {
                    BYTE* pY = pScanline0;
                    BYTE* pUV = pScanline0 + static_cast<ptrdiff_t>(dstH) * lPitch;
                    ConvertYUY2ToNV12(m_frameBuffer.data(), pY, lPitch, pUV, lPitch, dstW, dstH);
                }
            }
            sp2D2->Unlock2D();
            DWORD curLen = 0;
            if (SUCCEEDED(spBuffer->GetCurrentLength(&curLen)) && curLen == 0) {
                spBuffer->SetCurrentLength(sampleBytes);
            }
            copied = true;
        } else {
            static uint32_t s_lockFailures = 0;
            s_lockFailures++;
            if (s_lockFailures <= 5 || (s_lockFailures % 60) == 0) {
                LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> IMF2DBuffer2::Lock2DSize failed hr=0x%08X", hrLock);
            }
        }
    }

    if (!copied) {
        Microsoft::WRL::ComPtr<IMF2DBuffer> sp2D;
        if (SUCCEEDED(spBuffer.As(&sp2D))) {
            BYTE* pScanline0 = nullptr;
            LONG lPitch = 0;
            HRESULT hrLock = sp2D->Lock2D(&pScanline0, &lPitch);
            if (SUCCEEDED(hrLock) && pScanline0) {
                if (!isNv12) {
                    uint32_t srcStride = dstW * 2;
                    if (lPitch == static_cast<LONG>(srcStride)) {
                        memcpy(pScanline0, m_frameBuffer.data(), sampleBytes);
                    } else if (std::abs(lPitch) >= static_cast<LONG>(srcStride)) {
                        for (uint32_t y = 0; y < dstH; ++y) {
                            memcpy(pScanline0 + static_cast<ptrdiff_t>(y) * lPitch,
                                   m_frameBuffer.data() + static_cast<size_t>(y) * srcStride,
                                   srcStride);
                        }
                    }
                } else {
                    if (lPitch >= static_cast<LONG>(dstW) && dstH >= 2) {
                        BYTE* pY = pScanline0;
                        BYTE* pUV = pScanline0 + static_cast<ptrdiff_t>(dstH) * lPitch;
                        ConvertYUY2ToNV12(m_frameBuffer.data(), pY, lPitch, pUV, lPitch, dstW, dstH);
                    }
                }
                sp2D->Unlock2D();
                DWORD curLen = 0;
                if (SUCCEEDED(spBuffer->GetCurrentLength(&curLen)) && curLen == 0) {
                    spBuffer->SetCurrentLength(sampleBytes);
                }
                copied = true;
            } else {
                static uint32_t s_lock2DFailures = 0;
                s_lock2DFailures++;
                if (s_lock2DFailures <= 5 || (s_lock2DFailures % 60) == 0) {
                    LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> IMF2DBuffer::Lock2D failed hr=0x%08X", hrLock);
                }
            }
        }
    }

    if (!copied) {
        BYTE* pDstData = nullptr;
        DWORD dwMax = 0;
        HRESULT hrLock = spBuffer->Lock(&pDstData, &dwMax, nullptr);
        if (SUCCEEDED(hrLock) && pDstData) {
            if (dwMax >= sampleBytes) {
                if (!isNv12) {
                    memcpy(pDstData, m_frameBuffer.data(), sampleBytes);
                } else {
                    BYTE* pY = pDstData;
                    BYTE* pUV = pDstData + (dstW * dstH);
                    LONG pitch = static_cast<LONG>(dstW);
                    ConvertYUY2ToNV12(m_frameBuffer.data(), pY, pitch, pUV, pitch, dstW, dstH);
                }
            }
            spBuffer->Unlock();
            spBuffer->SetCurrentLength(sampleBytes);
            copied = true;
        } else {
            static uint32_t s_lockRawFailures = 0;
            s_lockRawFailures++;
            if (s_lockRawFailures <= 5 || (s_lockRawFailures % 60) == 0) {
                LogVcamDebug("GhostCamMediaStream::ProcessOneSample -> IMFMediaBuffer::Lock failed hr=0x%08X", hrLock);
            }
        }
    }

    if (!copied) {
        return;
    }

    LONGLONG nowTime = MFGetSystemTime();
    if (nowTime <= m_lastSampleTime) {
        nowTime = m_lastSampleTime + 1;
    }
    m_lastSampleTime = nowTime;

    spSample->SetSampleTime(nowTime);
    spSample->SetSampleDuration(m_frameDuration);

    if (pToken) {
        spSample->SetUnknown(MFSampleExtension_Token, pToken);
    }

    HRESULT hrQueue = m_spEventQueue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, spSample.Get());
    m_samplesDelivered++;
    if (m_samplesDelivered <= 5 || (m_samplesDelivered % 300) == 0) {
        LogVcamDebug("GhostCamMediaStream::ProcessOneSample #%llu -> Queue(MEMediaSample) hr=0x%08X shm=%d time=%lld alloc=%p",
                     m_samplesDelivered, hrQueue, isShmActive ? 1 : 0, nowTime, pAlloc);
    }
}

// Standby frame generation: animated dark-indigo gradient with moving glow bar
void GhostCamMediaStream::GenerateStandbyFrame(uint8_t* pDst, uint32_t width, uint32_t height, uint32_t frameIdx)
{
    if (!pDst || width == 0 || height == 0) return;

    int barCenter = static_cast<int>((frameIdx * 8) % (width + 300)) - 150;
    int barWidth = static_cast<int>(width / 12);
    if (barWidth < 40) barWidth = 40;

    uint32_t stride = width * 2;

    for (uint32_t y = 0; y < height; ++y) {
        uint8_t* pRow = pDst + y * stride;
        int baseY = 36 + static_cast<int>((y * 24) / height);
        bool inBadgeBand = (y >= (height * 44) / 100) && (y <= (height * 56) / 100);

        for (uint32_t x = 0; x < width; x += 2) {
            int distToBar = std::abs(static_cast<int>(x) - barCenter);
            int glow = 0;
            if (distToBar < barWidth) {
                int factor = barWidth - distToBar;
                glow = (factor * 60) / barWidth;
            }

            int yVal0 = baseY + glow;
            int yVal1 = baseY + glow;
            int uVal = 158 + (glow / 4);
            int vVal = 126 + (glow / 5);

            if (inBadgeBand) {
                yVal0 += 15;
                yVal1 += 15;
                uVal += 8;
            }

            if (y == (height * 44) / 100 || y == (height * 56) / 100) {
                yVal0 = 130 + glow;
                yVal1 = 130 + glow;
                uVal = 175;
                vVal = 135;
            }

            if (yVal0 > 235) yVal0 = 235;
            if (yVal1 > 235) yVal1 = 235;
            if (uVal > 240) uVal = 240;
            if (vVal > 240) vVal = 240;

            uint8_t* pMacro = pRow + x * 2;
            pMacro[0] = static_cast<uint8_t>(yVal0);
            pMacro[1] = static_cast<uint8_t>(uVal);
            pMacro[2] = static_cast<uint8_t>(yVal1);
            pMacro[3] = static_cast<uint8_t>(vVal);
        }
    }
}

// Fast integer YUY2 aspect-fit nearest/macro-pixel aligned scaler
void GhostCamMediaStream::ScaleYUY2(const uint8_t* pSrc, uint32_t srcW, uint32_t srcH,
                                    uint8_t* pDst, uint32_t dstW, uint32_t dstH)
{
    if (!pSrc || !pDst || srcW == 0 || srcH == 0 || dstW == 0 || dstH == 0) return;

    // Black YUY2: Y=16, U=128, Y=16, V=128 -> 0x80108010
    uint32_t blackPixel = 0x80108010;
    uint32_t totalPixels = dstW * dstH;
    uint32_t* pDst32 = reinterpret_cast<uint32_t*>(pDst);
    for (uint32_t i = 0; i < totalPixels / 2; ++i) {
        pDst32[i] = blackPixel;
    }

    uint64_t srcRatio = static_cast<uint64_t>(srcW) * dstH;
    uint64_t dstRatio = static_cast<uint64_t>(dstW) * srcH;

    uint32_t fitW = dstW;
    uint32_t fitH = dstH;
    uint32_t offsetX = 0;
    uint32_t offsetY = 0;

    if (srcRatio > dstRatio) {
        // Source is wider: letterbox top & bottom
        fitW = dstW;
        fitH = static_cast<uint32_t>((static_cast<uint64_t>(dstW) * srcH) / srcW);
        fitH = fitH & ~1;
        if (fitH == 0) fitH = 2;
        offsetY = (dstH > fitH) ? ((dstH - fitH) / 2) : 0;
    } else if (srcRatio < dstRatio) {
        // Source is taller: pillarbox left & right
        fitH = dstH;
        fitW = static_cast<uint32_t>((static_cast<uint64_t>(dstH) * srcW) / srcH);
        fitW = (fitW + 1) & ~1;
        if (fitW == 0) fitW = 2;
        offsetX = (dstW > fitW) ? (((dstW - fitW) / 2) & ~1) : 0;
    }

    uint32_t srcStride = srcW * 2;
    uint32_t dstStride = dstW * 2;

    for (uint32_t y = 0; y < fitH; ++y) {
        uint32_t targetDstY = offsetY + y;
        if (targetDstY >= dstH) break;

        uint32_t srcY = (y * srcH) / fitH;
        if (srcY >= srcH) srcY = srcH - 1;

        const uint8_t* pSrcRow = pSrc + srcY * srcStride;
        uint8_t* pDstRow = pDst + targetDstY * dstStride;

        for (uint32_t x = 0; x < fitW; x += 2) {
            uint32_t targetDstX0 = offsetX + x;
            uint32_t targetDstX1 = targetDstX0 + 1;
            if (targetDstX1 >= dstW) break;

            uint32_t srcX0 = (x * srcW) / fitW;
            uint32_t srcX1 = ((x + 1) * srcW) / fitW;
            if (srcX0 >= srcW) srcX0 = srcW - 1;
            if (srcX1 >= srcW) srcX1 = srcW - 1;

            uint32_t macro0 = srcX0 & ~1;
            uint32_t macro1 = srcX1 & ~1;

            const uint8_t* pM0 = pSrcRow + macro0 * 2;
            const uint8_t* pM1 = pSrcRow + macro1 * 2;

            uint8_t y0 = (srcX0 & 1) ? pM0[2] : pM0[0];
            uint8_t y1 = (srcX1 & 1) ? pM1[2] : pM1[0];

            uint8_t u = static_cast<uint8_t>((static_cast<uint32_t>(pM0[1]) + pM1[1]) / 2);
            uint8_t v = static_cast<uint8_t>((static_cast<uint32_t>(pM0[3]) + pM1[3]) / 2);

            uint8_t* pOutMacro = pDstRow + targetDstX0 * 2;
            pOutMacro[0] = y0;
            pOutMacro[1] = u;
            pOutMacro[2] = y1;
            pOutMacro[3] = v;
        }
    }
}

// Fast conversion from packed YUY2 to semi-planar NV12 with vertical chroma averaging
void GhostCamMediaStream::ConvertYUY2ToNV12(
    const uint8_t* pYuy2,
    uint8_t* pNv12Y,
    LONG pitchY,
    uint8_t* pNv12UV,
    LONG pitchUV,
    uint32_t width,
    uint32_t height)
{
    if (!pYuy2 || !pNv12Y || !pNv12UV || width == 0 || height == 0) return;

    const uint32_t srcStride = width * 2;
    const uint32_t halfHeight = height / 2;

    for (uint32_t uvRow = 0; uvRow < halfHeight; ++uvRow) {
        uint32_t yRow0 = uvRow * 2;
        uint32_t yRow1 = yRow0 + 1;

        const uint8_t* pSrcRow0 = pYuy2 + static_cast<size_t>(yRow0) * srcStride;
        const uint8_t* pSrcRow1 = pYuy2 + static_cast<size_t>(yRow1) * srcStride;

        uint8_t* pDstY0 = pNv12Y + static_cast<ptrdiff_t>(yRow0) * pitchY;
        uint8_t* pDstY1 = pNv12Y + static_cast<ptrdiff_t>(yRow1) * pitchY;
        uint8_t* pDstUV = pNv12UV + static_cast<ptrdiff_t>(uvRow) * pitchUV;

        for (uint32_t x = 0; x < width; x += 2) {
            uint8_t y0_r0 = pSrcRow0[0];
            uint8_t u0    = pSrcRow0[1];
            uint8_t y1_r0 = pSrcRow0[2];
            uint8_t v0    = pSrcRow0[3];

            uint8_t y0_r1 = pSrcRow1[0];
            uint8_t u1    = pSrcRow1[1];
            uint8_t y1_r1 = pSrcRow1[2];
            uint8_t v1    = pSrcRow1[3];

            pDstY0[x]     = y0_r0;
            pDstY0[x + 1] = y1_r0;
            pDstY1[x]     = y0_r1;
            pDstY1[x + 1] = y1_r1;

            pDstUV[x]     = static_cast<uint8_t>((static_cast<uint32_t>(u0) + u1 + 1) >> 1);
            pDstUV[x + 1] = static_cast<uint8_t>((static_cast<uint32_t>(v0) + v1 + 1) >> 1);

            pSrcRow0 += 4;
            pSrcRow1 += 4;
        }
    }

    if (height & 1) {
        uint32_t lastRow = height - 1;
        const uint8_t* pSrcRow = pYuy2 + static_cast<size_t>(lastRow) * srcStride;
        uint8_t* pDstY = pNv12Y + static_cast<ptrdiff_t>(lastRow) * pitchY;
        for (uint32_t x = 0; x < width; x += 2) {
            pDstY[x]     = pSrcRow[x * 2];
            pDstY[x + 1] = pSrcRow[x * 2 + 2];
        }
    }
}
