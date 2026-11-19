#pragma once

#ifndef WINVER
#define WINVER 0x0A00
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#ifndef NTDDI_VERSION
#define NTDDI_VERSION NTDDI_WIN10_NI
#endif

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>
#include <ksmedia.h>
#include <ksproxy.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <vector>
#include <queue>
#include <mutex>
#include <thread>
#include <atomic>
#include <chrono>

#include "SharedMemoryFormat.h"

// Forward declarations
class GhostCamMediaSource;
class GhostCamMediaStream;

#ifndef MFFrameSourceTypes_Color
#define MFFrameSourceTypes_Color 0x0001
#endif

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

// ============================================================================
// GhostCamMediaStream
// ============================================================================
class GhostCamMediaStream : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>>
{
public:
    GhostCamMediaStream();
    ~GhostCamMediaStream();

    HRESULT Initialize(GhostCamMediaSource* pSource, IMFStreamDescriptor* pStreamDesc);

    // Lifecycle controlled by source
    HRESULT Start(IMFMediaType* pMediaType, const PROPVARIANT* pvarStartPosition);
    HRESULT Stop();
    HRESULT Pause();
    HRESULT Shutdown();
    HRESULT GetStreamAttributes(IMFAttributes** ppAttributes);

    // IMFMediaStream2
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* pState) override;

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* pToken) override;

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

private:
    void WorkerThreadProc();
    void ProcessOneSample(IUnknown* pToken);
    void GenerateStandbyFrame(uint8_t* pDst, uint32_t width, uint32_t height, uint32_t frameIdx);
    void ScaleYUY2(const uint8_t* pSrc, uint32_t srcW, uint32_t srcH,
                   uint8_t* pDst, uint32_t dstW, uint32_t dstH);
    static void ConvertYUY2ToNV12(const uint8_t* pYuy2, uint8_t* pNv12Y, LONG pitchY, uint8_t* pNv12UV, LONG pitchUV, uint32_t width, uint32_t height);

    std::mutex m_mutex;
    std::mutex m_queueMutex;
    std::atomic<bool> m_isShutdown{ false };
    std::atomic<bool> m_isStreaming{ false };
    MF_STREAM_STATE m_streamState = MF_STREAM_STATE_STOPPED;

    GhostCamMediaSource* m_pSource = nullptr; // Raw weak pointer to parent, cleared on Shutdown
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_spEventQueue;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_spStreamDesc;
    Microsoft::WRL::ComPtr<IMFAttributes> m_spStreamAttributes;
    Microsoft::WRL::ComPtr<IMFMediaType> m_spCurrentMediaType;

    GUID m_subType = MFVideoFormat_YUY2;
    std::vector<uint8_t> m_frameBuffer;
    LONG m_lastShmSeq = -1;
    uint32_t m_lastShmW = 0;
    uint32_t m_lastShmH = 0;
    HANDLE m_hWaitTimer = NULL;

    uint32_t m_width = 1280;
    uint32_t m_height = 720;
    LONGLONG m_frameDuration = 166666; // 60 fps default in 100ns units
    LONGLONG m_sampleTime = 0;
    LONGLONG m_lastSampleTime = 0;
    uint64_t m_samplesDelivered = 0;
    uint32_t m_allocFailureCount = 0;
    uint32_t m_standbyFrameIndex = 0;

    std::queue<Microsoft::WRL::ComPtr<IUnknown>> m_tokenQueue;
    HANDLE m_hRequestEvent = NULL;
    HANDLE m_hStopEvent = NULL;
    std::thread m_workerThread;
};

// ============================================================================
// GhostCamMediaSource (Unified COM object: IMFActivate + IMFMediaSourceEx)
// ============================================================================
class GhostCamMediaSource : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
    Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>,
    IMFGetService,
    IKsControl,
    IMFSampleAllocatorControl>
{
public:
    GhostCamMediaSource();
    ~GhostCamMediaSource();

    HRESULT Initialize(IMFAttributes* pAttrs);

    GhostCamShmHeader* GetShmHeader() const { return m_pShmHeader; }
    HANDLE GetSyncEvent() const { return m_hSyncEvent; }
    IMFVideoSampleAllocator* GetAllocator() const { return m_spAllocator.Get(); }

    // IMFActivate
    IFACEMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    IFACEMETHODIMP ShutdownObject() override;
    IFACEMETHODIMP DetachObject() override;

    // IMFAttributes delegation
    IFACEMETHODIMP GetItem(REFGUID guidKey, PROPVARIANT* pValue) override;
    IFACEMETHODIMP GetItemType(REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) override;
    IFACEMETHODIMP CompareItem(REFGUID guidKey, REFPROPVARIANT Value, BOOL* pbResult) override;
    IFACEMETHODIMP Compare(IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE MatchType, BOOL* pbResult) override;
    IFACEMETHODIMP GetUINT32(REFGUID guidKey, UINT32* punValue) override;
    IFACEMETHODIMP GetUINT64(REFGUID guidKey, UINT64* punValue) override;
    IFACEMETHODIMP GetDouble(REFGUID guidKey, double* pfValue) override;
    IFACEMETHODIMP GetGUID(REFGUID guidKey, GUID* pguidValue) override;
    IFACEMETHODIMP GetStringLength(REFGUID guidKey, UINT32* pcchLength) override;
    IFACEMETHODIMP GetString(REFGUID guidKey, LPWSTR pwszValue, UINT32 cchBufSize, UINT32* pcchLength) override;
    IFACEMETHODIMP GetAllocatedString(REFGUID guidKey, LPWSTR* ppwszValue, UINT32* pcchLength) override;
    IFACEMETHODIMP GetBlobSize(REFGUID guidKey, UINT32* pcbBlobSize) override;
    IFACEMETHODIMP GetBlob(REFGUID guidKey, UINT8* pBuf, UINT32 cbBufSize, UINT32* pcbBlobSize) override;
    IFACEMETHODIMP GetAllocatedBlob(REFGUID guidKey, UINT8** ppBuf, UINT32* pcbSize) override;
    IFACEMETHODIMP GetUnknown(REFGUID guidKey, REFIID riid, LPVOID* ppv) override;
    IFACEMETHODIMP SetItem(REFGUID guidKey, REFPROPVARIANT Value) override;
    IFACEMETHODIMP DeleteItem(REFGUID guidKey) override;
    IFACEMETHODIMP DeleteAllItems() override;
    IFACEMETHODIMP SetUINT32(REFGUID guidKey, UINT32 unValue) override;
    IFACEMETHODIMP SetUINT64(REFGUID guidKey, UINT64 unValue) override;
    IFACEMETHODIMP SetDouble(REFGUID guidKey, double fValue) override;
    IFACEMETHODIMP SetGUID(REFGUID guidKey, REFGUID guidValue) override;
    IFACEMETHODIMP SetString(REFGUID guidKey, LPCWSTR wszValue) override;
    IFACEMETHODIMP SetBlob(REFGUID guidKey, const UINT8* pBuf, UINT32 cbBufSize) override;
    IFACEMETHODIMP SetUnknown(REFGUID guidKey, IUnknown* pUnknown) override;
    IFACEMETHODIMP LockStore() override;
    IFACEMETHODIMP UnlockStore() override;
    IFACEMETHODIMP GetCount(UINT32* pcItems) override;
    IFACEMETHODIMP GetItemByIndex(UINT32 unIndex, GUID* pguidKey, PROPVARIANT* pValue) override;
    IFACEMETHODIMP CopyAllItems(IMFAttributes* pDest) override;

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) override;
    IFACEMETHODIMP Stop() override;
    IFACEMETHODIMP Pause() override;
    IFACEMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) override;
    IFACEMETHODIMP SetD3DManager(IUnknown* pManager) override;

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

    // IKsControl
    IFACEMETHODIMP KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override;

    // IMFSampleAllocatorControl
    IFACEMETHODIMP SetDefaultAllocator(DWORD dwOutputStreamID, IUnknown* pAllocator) override;
    IFACEMETHODIMP GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID, MFSampleAllocatorUsage* peUsage) override;

private:
    HRESULT InitializeLocked(IMFAttributes* pAttrs);

    std::mutex m_mutex;
    bool m_initialized = false;
    bool m_isShutdown = false;
    bool m_allocatorInitialized = false;
    bool m_streamCreatedEventSent = false;

    Microsoft::WRL::ComPtr<IMFAttributes> m_spAttributes;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_spEventQueue;
    Microsoft::WRL::ComPtr<IMFPresentationDescriptor> m_spPresDesc;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_spStreamDesc;
    Microsoft::WRL::ComPtr<GhostCamMediaStream> m_spStream;
    Microsoft::WRL::ComPtr<IUnknown> m_spD3DManager;
    Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> m_spAllocator;

    HANDLE m_hShmFile = INVALID_HANDLE_VALUE;
    HANDLE m_hShmMapping = NULL;
    void* m_pShmView = nullptr;
    GhostCamShmHeader* m_pShmHeader = nullptr;
    HANDLE m_hSyncEvent = NULL;
};
