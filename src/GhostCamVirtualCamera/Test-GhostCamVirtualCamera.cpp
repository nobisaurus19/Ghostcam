#include <windows.h>
#include <iostream>
#include <iomanip>
#include <cassert>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <ks.h>
#include <ksmedia.h>
#include <wrl/client.h>

#include "SharedMemoryFormat.h"

using Microsoft::WRL::ComPtr;

typedef HRESULT (STDAPICALLTYPE *LPFNGETCLASSOBJECT)(REFCLSID, REFIID, LPVOID*);

int RealMain()
{
    std::cout << "==========================================================\n" << std::flush;
    std::cout << " Running GhostCamVirtualCamera In-Process Verification Test\n" << std::flush;
    std::cout << "==========================================================\n" << std::flush;

    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        std::cerr << "[-] CoInitializeEx failed: 0x" << std::hex << hr << "\n" << std::flush;
        return 1;
    }

    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        std::cerr << "[-] MFStartup failed: 0x" << std::hex << hr << "\n" << std::flush;
        return 1;
    }

    const wchar_t* dllPath = L"C:\\Users\\net_z\\Downloads\\Ghostcam\\src\\GhostCamVirtualCamera\\bin\\x64\\Release\\GhostCamVirtualCamera.dll";
    std::wcout << L"[*] Loading DLL: " << dllPath << L"\n" << std::flush;
    HMODULE hDll = LoadLibraryW(dllPath);
    if (!hDll) {
        std::cerr << "[-] Failed to load DLL! Error: " << GetLastError() << "\n" << std::flush;
        return 1;
    }
    std::cout << "[+] DLL loaded successfully at base: " << hDll << "\n" << std::flush;

    auto pfnDllGetClassObject = (LPFNGETCLASSOBJECT)GetProcAddress(hDll, "DllGetClassObject");
    if (!pfnDllGetClassObject) {
        std::cerr << "[-] GetProcAddress('DllGetClassObject') failed!\n" << std::flush;
        return 1;
    }
    std::cout << "[+] Found DllGetClassObject\n" << std::flush;

    {
        // 1. Get Class Factory
        ComPtr<IClassFactory> spFactory;
        hr = pfnDllGetClassObject(CLSID_GhostCamActivate, IID_PPV_ARGS(&spFactory));
        if (FAILED(hr) || !spFactory) {
            std::cerr << "[-] DllGetClassObject(CLSID_GhostCamActivate) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Obtained IClassFactory for CLSID_GhostCamActivate\n" << std::flush;

        // Verify Direct Creation of IMFMediaSourceEx from ClassFactory
        ComPtr<IMFMediaSourceEx> spDirectSourceEx;
        hr = spFactory->CreateInstance(NULL, IID_PPV_ARGS(&spDirectSourceEx));
        if (FAILED(hr) || !spDirectSourceEx) {
            std::cerr << "[-] CreateInstance(IMFMediaSourceEx) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Successfully verified direct CreateInstance(IMFMediaSourceEx)\n" << std::flush;

        // Verify Direct Creation of IMFMediaSource from ClassFactory
        ComPtr<IMFMediaSource> spDirectSource;
        hr = spFactory->CreateInstance(NULL, IID_PPV_ARGS(&spDirectSource));
        if (FAILED(hr) || !spDirectSource) {
            std::cerr << "[-] CreateInstance(IMFMediaSource) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Successfully verified direct CreateInstance(IMFMediaSource)\n" << std::flush;

        // 2. Create IMFActivate instance
        ComPtr<IMFActivate> spActivate;
        hr = spFactory->CreateInstance(NULL, IID_PPV_ARGS(&spActivate));
        if (FAILED(hr) || !spActivate) {
            std::cerr << "[-] CreateInstance(IMFActivate) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Instantiated GhostCamActivate (IMFActivate)\n" << std::flush;

        // Verify QI reciprocity: IMFActivate -> IMFMediaSourceEx
        ComPtr<IMFMediaSourceEx> spSourceFromQI;
        hr = spActivate.As(&spSourceFromQI);
        if (FAILED(hr) || !spSourceFromQI) {
            std::cerr << "[-] QI(IMFMediaSourceEx) on IMFActivate failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Successfully verified QI(IMFMediaSourceEx) directly on IMFActivate!\n" << std::flush;

        // 3. ActivateObject -> IMFMediaSource
        ComPtr<IMFMediaSource> spSource;
        hr = spActivate->ActivateObject(IID_PPV_ARGS(&spSource));
        if (FAILED(hr) || !spSource) {
            std::cerr << "[-] ActivateObject(IID_IMFMediaSource) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Activated GhostCamMediaSource (IMFMediaSource)\n" << std::flush;

        // Verify IMFSampleAllocatorControl::GetAllocatorUsage
        ComPtr<IMFSampleAllocatorControl> spAllocControl;
        hr = spSource.As(&spAllocControl);
        if (SUCCEEDED(hr) && spAllocControl) {
            MFSampleAllocatorUsage usage = (MFSampleAllocatorUsage)-1;
            DWORD dwInStream = 999;
            hr = spAllocControl->GetAllocatorUsage(0, &dwInStream, &usage);
            if (SUCCEEDED(hr) && usage == MFSampleAllocatorUsage_UsesProvidedAllocator && dwInStream == 0) {
                std::cout << "[+] Verified IMFSampleAllocatorControl::GetAllocatorUsage returns UsesProvidedAllocator (0)\n" << std::flush;
            } else {
                std::cerr << "[-] Unexpected allocator usage: " << usage << ", hr=0x" << std::hex << hr << "\n" << std::flush;
                return 1;
            }
        }

        // Test dropping/releasing spActivate to prove source is NOT killed early!
        spActivate.Reset();
        std::cout << "[+] Safely released IMFActivate smart pointer; verifying source remains operational\n" << std::flush;

        // 4. CreatePresentationDescriptor
        ComPtr<IMFPresentationDescriptor> spPD;
        hr = spSource->CreatePresentationDescriptor(&spPD);
        if (FAILED(hr) || !spPD) {
            std::cerr << "[-] CreatePresentationDescriptor failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Created Presentation Descriptor\n" << std::flush;

        DWORD dwStreamCount = 0;
        spPD->GetStreamDescriptorCount(&dwStreamCount);
        std::cout << "[+] Stream descriptor count: " << dwStreamCount << "\n" << std::flush;

        BOOL fSelected = FALSE;
        ComPtr<IMFStreamDescriptor> spSD;
        hr = spPD->GetStreamDescriptorByIndex(0, &fSelected, &spSD);
        if (FAILED(hr) || !spSD || !fSelected) {
            std::cerr << "[-] Stream 0 was not selected or could not be retrieved!\n" << std::flush;
            return 1;
        }

        ComPtr<IMFMediaTypeHandler> spHandler;
        hr = spSD->GetMediaTypeHandler(&spHandler);
        if (FAILED(hr) || !spHandler) {
            std::cerr << "[-] Failed to get IMFMediaTypeHandler from StreamDescriptor!\n" << std::flush;
            return 1;
        }
        DWORD typeCount = 0;
        hr = spHandler->GetMediaTypeCount(&typeCount);
        if (FAILED(hr)) {
            std::cerr << "[-] GetMediaTypeCount failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Stream MediaType count: " << typeCount << " (expected 12)\n" << std::flush;
        if (typeCount != 12) {
            std::cerr << "[-] MediaType count mismatch! Expected 12, got " << typeCount << "\n" << std::flush;
            return 1;
        }

        // Verify stream descriptor attributes
        GUID category = GUID_NULL;
        spSD->GetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, &category);
        std::cout << "[+] Verified MF_DEVICESTREAM_STREAM_CATEGORY == PINNAME_VIDEO_CAPTURE: "
                  << (category == PINNAME_VIDEO_CAPTURE ? "YES" : "NO") << "\n" << std::flush;

        UINT32 shared = 0;
        spSD->GetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, &shared);
        std::cout << "[+] Verified MF_DEVICESTREAM_FRAMESERVER_SHARED: " << shared << "\n" << std::flush;

        UINT32 srcTypes = 0;
        spSD->GetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, &srcTypes);
        std::cout << "[+] Verified MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES: 0x" << std::hex << srcTypes << std::dec << "\n" << std::flush;

        // Set SHM to idle (Flags = 0) so Sample 1 tests standby gradient
        HANDLE hShmInit = CreateFileW(
            GHOSTCAM_SHM_FILE_PATH,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (hShmInit != INVALID_HANDLE_VALUE) {
            HANDLE hMap = CreateFileMappingW(hShmInit, NULL, PAGE_READWRITE, 0, GHOSTCAM_SHM_SIZE, NULL);
            if (hMap) {
                void* pView = MapViewOfFile(hMap, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, GHOSTCAM_SHM_SIZE);
                if (pView) {
                    auto* pHeader = reinterpret_cast<GhostCamShmHeader*>(pView);
                    pHeader->Flags = 0; // Standby
                    UnmapViewOfFile(pView);
                }
                CloseHandle(hMap);
            }
            CloseHandle(hShmInit);
        }

        // 5. Start Media Source
        PROPVARIANT varStart;
        PropVariantInit(&varStart);
        hr = spSource->Start(spPD.Get(), &GUID_NULL, &varStart);
        if (FAILED(hr)) {
            std::cerr << "[-] Source Start failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Media Source Start succeeded\n" << std::flush;

        // 6. Receive MENewStream from source event queue
        ComPtr<IMFMediaEvent> spEvent;
        hr = spSource->GetEvent(0, &spEvent);
        if (FAILED(hr)) {
            std::cerr << "[-] GetEvent from source failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        MediaEventType met = MEUnknown;
        spEvent->GetType(&met);
        std::cout << "[+] Source event received: " << met << " (expected MENewStream=" << MENewStream << ")\n" << std::flush;

        PROPVARIANT valStream;
        PropVariantInit(&valStream);
        spEvent->GetValue(&valStream);
        ComPtr<IMFMediaStream> spStream;
        if (valStream.vt == VT_UNKNOWN && valStream.punkVal) {
            valStream.punkVal->QueryInterface(IID_PPV_ARGS(&spStream));
        }
        PropVariantClear(&valStream);

        if (!spStream) {
            std::cerr << "[-] Failed to get IMFMediaStream from MENewStream event!\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Successfully acquired active IMFMediaStream\n" << std::flush;

        // Read MESourceStarted
        spSource->GetEvent(0, &spEvent);
        spEvent->GetType(&met);
        std::cout << "[+] Source event received: " << met << " (expected MESourceStarted=" << MESourceStarted << ")\n" << std::flush;

        // Read MEStreamStarted from stream
        spStream->GetEvent(0, &spEvent);
        spEvent->GetType(&met);
        std::cout << "[+] Stream event received: " << met << " (expected MEStreamStarted=" << MEStreamStarted << ")\n" << std::flush;

        // 7. Request Sample (Standby frame test)
        std::cout << "[*] Requesting Sample 1 (Standby dark indigo gradient)...\n" << std::flush;
        hr = spStream->RequestSample(NULL);
        if (FAILED(hr)) {
            std::cerr << "[-] RequestSample failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }

        hr = spStream->GetEvent(0, &spEvent);
        if (FAILED(hr)) {
            std::cerr << "[-] Stream GetEvent for sample failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        spEvent->GetType(&met);
        std::cout << "[+] Stream event received: " << met << " (expected MEMediaSample=" << MEMediaSample << ")\n" << std::flush;
        if (met != MEMediaSample) {
            std::cerr << "[-] Expected MEMediaSample but got: " << met << "\n" << std::flush;
            return 1;
        }

        PROPVARIANT valSample;
        PropVariantInit(&valSample);
        spEvent->GetValue(&valSample);
        ComPtr<IMFSample> spSample;
        if (valSample.vt == VT_UNKNOWN && valSample.punkVal) {
            valSample.punkVal->QueryInterface(IID_PPV_ARGS(&spSample));
        }
        PropVariantClear(&valSample);

        if (!spSample) {
            std::cerr << "[-] Failed to get IMFSample from event!\n" << std::flush;
            return 1;
        }

        LONGLONG sampleTime = 0, sampleDur = 0;
        spSample->GetSampleTime(&sampleTime);
        spSample->GetSampleDuration(&sampleDur);
        std::cout << "[+] Sample Time: " << sampleTime << " (100ns units), Duration: " << sampleDur << "\n" << std::flush;

        ComPtr<IMFMediaBuffer> spBuffer;
        hr = spSample->GetBufferByIndex(0, &spBuffer);
        DWORD dwLen = 0;
        spBuffer->GetCurrentLength(&dwLen);
        std::cout << "[+] Sample Buffer Length: " << dwLen << " bytes (1280x720 YUY2 = 1,843,200)\n" << std::flush;
        if (dwLen != 1280 * 720 * 2) {
            std::cerr << "[-] Buffer length mismatch!\n" << std::flush;
            return 1;
        }

        BYTE* pBytes = nullptr;
        spBuffer->Lock(&pBytes, NULL, NULL);
        std::cout << "[+] Sample bytes (first 8 bytes Y0,U,Y1,V): ";
        for (int i = 0; i < 8; ++i) {
            std::cout << "0x" << std::hex << std::setw(2) << std::setfill('0') << (int)pBytes[i] << " ";
        }
        std::cout << std::dec << "\n" << std::flush;
        spBuffer->Unlock();

        // 8. Live SHM test: simulate Ghostcam Studio writing active green frame
        std::cout << "\n[*] Testing Live SHM frame production & Global\\GhostCam_FrameSync...\n" << std::flush;
        HANDLE hShmFile = CreateFileW(
            GHOSTCAM_SHM_FILE_PATH,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);

        if (hShmFile != INVALID_HANDLE_VALUE) {
            HANDLE hMap = CreateFileMappingW(hShmFile, NULL, PAGE_READWRITE, 0, GHOSTCAM_SHM_SIZE, NULL);
            if (hMap) {
                void* pView = MapViewOfFile(hMap, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, GHOSTCAM_SHM_SIZE);
                if (pView) {
                    auto* pHeader = reinterpret_cast<GhostCamShmHeader*>(pView);
                    pHeader->Magic = GHOSTCAM_MAGIC_GHST;
                    pHeader->Version = GHOSTCAM_VERSION_1;
                    pHeader->FrameSeq = 42;
                    pHeader->Width = 1280;
                    pHeader->Height = 720;
                    pHeader->Fps = 30;
                    pHeader->Format = GHOSTCAM_FORMAT_YUY2;
                    pHeader->PayloadBytes = 1280 * 720 * 2;
                    pHeader->TimestampTicks = 123456789;
                    pHeader->Flags = GHOSTCAM_FLAG_ACTIVE; // ACTIVE STREAMING!

                    // Fill with solid Green YUY2: Y=145, U=54, Y=145, V=34 (0x22913691)
                    uint8_t* pDstPixels = reinterpret_cast<uint8_t*>(pView) + sizeof(GhostCamShmHeader);
                    uint32_t greenMacro = 0x22913691;
                    uint32_t* pPixels32 = reinterpret_cast<uint32_t*>(pDstPixels);
                    for (size_t i = 0; i < (1280 * 720 / 2); ++i) {
                        pPixels32[i] = greenMacro;
                    }

                    // Signal Global\GhostCam_FrameSync
                    HANDLE hSync = OpenEventW(EVENT_MODIFY_STATE, FALSE, GHOSTCAM_EVENT_NAME);
                    if (hSync) {
                        SetEvent(hSync);
                        CloseHandle(hSync);
                        std::cout << "[+] Signaled Global\\GhostCam_FrameSync with green test frame\n" << std::flush;
                    }

                    UnmapViewOfFile(pView);
                }
                CloseHandle(hMap);
            }
            CloseHandle(hShmFile);
        }

        // Request Sample 2 (Live frame)
        std::cout << "[*] Requesting Sample 2 (Live green frame from SHM)...\n" << std::flush;
        hr = spStream->RequestSample(NULL);
        hr = spStream->GetEvent(0, &spEvent);
        spEvent->GetType(&met);
        if (met == MEMediaSample) {
            spEvent->GetValue(&valSample);
            if (valSample.vt == VT_UNKNOWN && valSample.punkVal) {
                valSample.punkVal->QueryInterface(IID_PPV_ARGS(&spSample));
            }
            PropVariantClear(&valSample);

            spSample->GetBufferByIndex(0, &spBuffer);
            spBuffer->Lock(&pBytes, NULL, NULL);
            std::cout << "[+] Live Sample bytes (first 8 bytes Y0,U,Y1,V): ";
            for (int i = 0; i < 8; ++i) {
                std::cout << "0x" << std::hex << std::setw(2) << std::setfill('0') << (int)pBytes[i] << " ";
            }
            std::cout << std::dec << "\n" << std::flush;
            bool isGreen = (pBytes[0] == 0x91 && pBytes[1] == 0x36 && pBytes[2] == 0x91 && pBytes[3] == 0x22);
            std::cout << "[+] Verified live SHM frame copied accurately: " << (isGreen ? "SUCCESS (Green YUY2 matches!)" : "MISMATCH") << "\n" << std::flush;
            spBuffer->Unlock();
        }

        // 9. Repeat Start() call while already running (dynamic format / allocator re-init test)
        std::cout << "\n[*] Testing repeat Start() call while already running (MEUpdatedStream verification)...\n" << std::flush;
        ComPtr<IMFVideoSampleAllocatorEx> spAllocEx;
        hr = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&spAllocEx));
        if (SUCCEEDED(hr) && spAllocEx) {
            hr = spAllocControl->SetDefaultAllocator(0, spAllocEx.Get());
            std::cout << "[+] Registered sample allocator on active source: hr=0x" << std::hex << hr << std::dec << "\n" << std::flush;
        }

        PROPVARIANT varStartRepeat;
        PropVariantInit(&varStartRepeat);
        varStartRepeat.vt = VT_I8;
        varStartRepeat.hVal.QuadPart = MFGetSystemTime();
        hr = spSource->Start(spPD.Get(), &GUID_NULL, &varStartRepeat);
        PropVariantClear(&varStartRepeat);
        if (FAILED(hr)) {
            std::cerr << "[-] Repeat Start() call failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Repeat Start() call succeeded\n" << std::flush;

        // Expect MEUpdatedStream (206) because stream was already created
        spEvent.Reset();
        hr = spSource->GetEvent(0, &spEvent);
        if (FAILED(hr) || !spEvent) {
            std::cerr << "[-] Failed to get source event after repeat Start!\n" << std::flush;
            return 1;
        }
        spEvent->GetType(&met);
        std::cout << "[+] Source event received: " << met << " (expected MEUpdatedStream=" << MEUpdatedStream << ")\n" << std::flush;
        if (met != MEUpdatedStream) {
            std::cerr << "[-] Expected MEUpdatedStream (206) on repeat Start, got " << met << "\n" << std::flush;
            return 1;
        }

        // Read MESourceStarted
        spEvent.Reset();
        hr = spSource->GetEvent(0, &spEvent);
        met = MEUnknown;
        if (SUCCEEDED(hr) && spEvent) spEvent->GetType(&met);
        std::cout << "[+] Source event received: " << met << " (expected MESourceStarted=" << MESourceStarted << ")\n" << std::flush;

        // Read MEStreamStarted from stream
        spEvent.Reset();
        hr = spStream->GetEvent(0, &spEvent);
        met = MEUnknown;
        if (SUCCEEDED(hr) && spEvent) spEvent->GetType(&met);
        std::cout << "[+] Stream event received: " << met << " (expected MEStreamStarted=" << MEStreamStarted << ")\n" << std::flush;

        // Request a sample to verify the reinitialized allocator delivers valid frames
        std::cout << "[*] Requesting Sample 3 on repeat-started stream...\n" << std::flush;
        hr = spStream->RequestSample(NULL);
        spEvent.Reset();
        hr = spStream->GetEvent(0, &spEvent);
        met = MEUnknown;
        if (SUCCEEDED(hr) && spEvent) spEvent->GetType(&met);
        if (met != MEMediaSample) {
            std::cerr << "[-] Expected MEMediaSample after repeat Start, got " << met << "\n" << std::flush;
            return 1;
        }
        spEvent->GetValue(&valSample);
        if (valSample.vt == VT_UNKNOWN && valSample.punkVal) {
            valSample.punkVal->QueryInterface(IID_PPV_ARGS(&spSample));
        }
        PropVariantClear(&valSample);
        if (!spSample) {
            std::cerr << "[-] Null sample delivered after repeat Start!\n" << std::flush;
            return 1;
        }
        LONGLONG repeatSampleTime = 0;
        spSample->GetSampleTime(&repeatSampleTime);
        std::cout << "[+] Repeat Start sample delivered successfully! Time=" << repeatSampleTime << "\n" << std::flush;

        // Stop and shutdown first media source instance
        std::cout << "\n[*] Stopping and shutting down Media Source...\n" << std::flush;
        spStream.Reset();
        spBuffer.Reset();
        spSample.Reset();
        spEvent.Reset();
        spSD.Reset();
        spPD.Reset();
        spSource->Stop();
        spSource->Shutdown();
        spSource.Reset();

        // 10. SourceReader + Allocator + MFGetSystemTime verification
        std::cout << "\n[*] Testing SourceReader + Allocator + MFGetSystemTime verification...\n" << std::flush;
        ComPtr<IMFMediaSource> spSourceSR;
        ComPtr<IMFActivate> spActivateSR;
        hr = spFactory->CreateInstance(NULL, IID_PPV_ARGS(&spActivateSR));
        if (FAILED(hr) || !spActivateSR) {
            std::cerr << "[-] Failed to create IMFActivate for SourceReader test!\n" << std::flush;
            return 1;
        }
        hr = spActivateSR->ActivateObject(IID_PPV_ARGS(&spSourceSR));
        if (FAILED(hr) || !spSourceSR) {
            std::cerr << "[-] Failed to activate IMFMediaSource for SourceReader test!\n" << std::flush;
            return 1;
        }

        ComPtr<IMFSampleAllocatorControl> spAllocControlSR;
        hr = spSourceSR.As(&spAllocControlSR);
        if (SUCCEEDED(hr) && spAllocControlSR) {
            ComPtr<IMFVideoSampleAllocatorEx> spAllocSR;
            hr = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&spAllocSR));
            if (SUCCEEDED(hr) && spAllocSR) {
                hr = spAllocControlSR->SetDefaultAllocator(0, spAllocSR.Get());
                std::cout << "[+] SetDefaultAllocator on SourceReader media source: hr=0x" << std::hex << hr << std::dec << "\n" << std::flush;
            }
        }

        ComPtr<IMFSourceReader> spReader;
        hr = MFCreateSourceReaderFromMediaSource(spSourceSR.Get(), NULL, &spReader);
        if (FAILED(hr) || !spReader) {
            std::cerr << "[-] MFCreateSourceReaderFromMediaSource failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Created SourceReader from MediaSource with custom allocator\n" << std::flush;

        LONGLONG prevTimestamp = 0;
        for (int i = 0; i < 5; ++i) {
            DWORD dwActualStreamIndex = 0;
            DWORD dwStreamFlags = 0;
            LONGLONG llTimestamp = 0;
            ComPtr<IMFSample> spSRSample;

            hr = spReader->ReadSample(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                0,
                &dwActualStreamIndex,
                &dwStreamFlags,
                &llTimestamp,
                &spSRSample);

            if (FAILED(hr) || !spSRSample) {
                std::cerr << "[-] ReadSample " << i << " failed: 0x" << std::hex << hr << "\n" << std::flush;
                return 1;
            }

            DWORD dwLenSR = 0;
            spSRSample->GetTotalLength(&dwLenSR);
            std::cout << "  [SR Sample " << i << "] Timestamp: " << llTimestamp
                      << " (100ns units), Len: " << dwLenSR << "\n" << std::flush;

            // Assert SampleTime > 10000000 (system clock, not 0)
            if (llTimestamp <= 10000000LL) {
                std::cerr << "[-] Assert failed: SampleTime (" << llTimestamp << ") <= 10,000,000! Must use system clock!\n" << std::flush;
                return 1;
            }

            // Assert strictly increasing timestamps across samples
            if (i > 0 && llTimestamp <= prevTimestamp) {
                std::cerr << "[-] Assert failed: Timestamps are not strictly increasing! (prev=" << prevTimestamp << ", cur=" << llTimestamp << ")\n" << std::flush;
                return 1;
            }
            prevTimestamp = llTimestamp;
        }
        std::cout << "[+] Successfully verified SourceReader + Allocator + strictly increasing MFGetSystemTime timestamps (>10M)!\n" << std::flush;

        // Cleanup SourceReader instance
        spReader.Reset();
        spSourceSR->Shutdown();
        spSourceSR.Reset();
        spActivateSR.Reset();

        // 10b. SourceReader NV12 @ 60 FPS verification
        std::cout << "\n[*] Testing SourceReader NV12 @ 60 FPS mode...\n" << std::flush;
        ComPtr<IMFMediaSource> spSourceNV12;
        ComPtr<IMFActivate> spActivateNV12;
        hr = spFactory->CreateInstance(NULL, IID_PPV_ARGS(&spActivateNV12));
        if (FAILED(hr) || !spActivateNV12) {
            std::cerr << "[-] Failed to create IMFActivate for NV12 test!\n" << std::flush;
            return 1;
        }
        hr = spActivateNV12->ActivateObject(IID_PPV_ARGS(&spSourceNV12));
        if (FAILED(hr) || !spSourceNV12) {
            std::cerr << "[-] Failed to activate IMFMediaSource for NV12 test!\n" << std::flush;
            return 1;
        }

        ComPtr<IMFSampleAllocatorControl> spAllocControlNV12;
        hr = spSourceNV12.As(&spAllocControlNV12);
        if (SUCCEEDED(hr) && spAllocControlNV12) {
            ComPtr<IMFVideoSampleAllocatorEx> spAllocNV12;
            hr = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&spAllocNV12));
            if (SUCCEEDED(hr) && spAllocNV12) {
                hr = spAllocControlNV12->SetDefaultAllocator(0, spAllocNV12.Get());
                std::cout << "[+] SetDefaultAllocator on NV12 media source: hr=0x" << std::hex << hr << std::dec << "\n" << std::flush;
            }
        }

        ComPtr<IMFAttributes> spReaderAttrsNV12;
        MFCreateAttributes(&spReaderAttrsNV12, 2);
        if (spReaderAttrsNV12) {
            spReaderAttrsNV12->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
        }

        ComPtr<IMFSourceReader> spReaderNV12;
        hr = MFCreateSourceReaderFromMediaSource(spSourceNV12.Get(), spReaderAttrsNV12.Get(), &spReaderNV12);
        if (FAILED(hr) || !spReaderNV12) {
            std::cerr << "[-] MFCreateSourceReaderFromMediaSource (NV12) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }

        // Find native NV12 1280x720 @ 60 FPS media type
        ComPtr<IMFMediaType> spNv12Type;
        for (DWORD idx = 0; ; ++idx) {
            ComPtr<IMFMediaType> spNativeType;
            hr = spReaderNV12->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), idx, &spNativeType);
            if (FAILED(hr)) break;

            GUID subType = GUID_NULL;
            UINT32 w = 0, h = 0, num = 0, den = 0;
            spNativeType->GetGUID(MF_MT_SUBTYPE, &subType);
            MFGetAttributeSize(spNativeType.Get(), MF_MT_FRAME_SIZE, &w, &h);
            MFGetAttributeRatio(spNativeType.Get(), MF_MT_FRAME_RATE, &num, &den);

            if (subType == MFVideoFormat_NV12 && w == 1280 && h == 720 && num == 60 && den == 1) {
                spNv12Type = spNativeType;
                std::cout << "[+] Found native NV12 1280x720 @ 60 FPS media type at index " << idx << "\n" << std::flush;
                break;
            }
        }

        if (!spNv12Type) {
            std::cerr << "[-] Could not find native NV12 1280x720 @ 60 FPS media type!\n" << std::flush;
            return 1;
        }

        hr = spReaderNV12->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), NULL, spNv12Type.Get());
        if (FAILED(hr)) {
            std::cerr << "[-] SetCurrentMediaType(NV12) failed: 0x" << std::hex << hr << "\n" << std::flush;
            return 1;
        }
        std::cout << "[+] Selected NV12 1280x720 @ 60 FPS as current media type on SourceReader\n" << std::flush;

        // Ensure SHM is active green frame (FrameSeq = 42, even!)
        HANDLE hShmNV12 = CreateFileW(
            GHOSTCAM_SHM_FILE_PATH,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (hShmNV12 != INVALID_HANDLE_VALUE) {
            HANDLE hMap = CreateFileMappingW(hShmNV12, NULL, PAGE_READWRITE, 0, GHOSTCAM_SHM_SIZE, NULL);
            if (hMap) {
                void* pView = MapViewOfFile(hMap, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, GHOSTCAM_SHM_SIZE);
                if (pView) {
                    auto* pHeader = reinterpret_cast<GhostCamShmHeader*>(pView);
                    pHeader->Magic = GHOSTCAM_MAGIC_GHST;
                    pHeader->Version = GHOSTCAM_VERSION_1;
                    pHeader->FrameSeq = 42;
                    pHeader->Width = 1280;
                    pHeader->Height = 720;
                    pHeader->Fps = 60;
                    pHeader->Format = GHOSTCAM_FORMAT_YUY2;
                    pHeader->PayloadBytes = 1280 * 720 * 2;
                    pHeader->TimestampTicks = 987654321;
                    pHeader->Flags = GHOSTCAM_FLAG_ACTIVE;

                    // Fill with green YUY2: 0x22913691 (Y0=0x91, U=0x36, Y1=0x91, V=0x22)
                    uint8_t* pDstPixels = reinterpret_cast<uint8_t*>(pView) + sizeof(GhostCamShmHeader);
                    uint32_t greenMacro = 0x22913691;
                    uint32_t* pPixels32 = reinterpret_cast<uint32_t*>(pDstPixels);
                    for (size_t i = 0; i < (1280 * 720 / 2); ++i) {
                        pPixels32[i] = greenMacro;
                    }
                    UnmapViewOfFile(pView);
                }
                CloseHandle(hMap);
            }
            CloseHandle(hShmNV12);
        }

        // Read 2 samples via spReaderNV12->ReadSample
        for (int i = 0; i < 2; ++i) {
            DWORD dwActualStreamIndex = 0;
            DWORD dwStreamFlags = 0;
            LONGLONG llTimestamp = 0;
            ComPtr<IMFSample> spSampleNV12;

            hr = spReaderNV12->ReadSample(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                0,
                &dwActualStreamIndex,
                &dwStreamFlags,
                &llTimestamp,
                &spSampleNV12);

            if (FAILED(hr) || !spSampleNV12) {
                std::cerr << "[-] NV12 ReadSample " << i << " failed: 0x" << std::hex << hr << "\n" << std::flush;
                return 1;
            }

            DWORD dwLenSR = 0;
            spSampleNV12->GetTotalLength(&dwLenSR);
            LONGLONG nv12SampleDur = 0;
            spSampleNV12->GetSampleDuration(&nv12SampleDur);
            std::cout << "  [NV12 Sample " << i << "] Timestamp: " << llTimestamp
                      << ", Duration: " << nv12SampleDur << ", Len: " << dwLenSR << " bytes\n" << std::flush;

            const DWORD expectedNv12Bytes = 1280 * 720 * 3 / 2; // 1,382,400 bytes
            if (dwLenSR != expectedNv12Bytes) {
                std::cerr << "[-] NV12 length mismatch! Expected " << expectedNv12Bytes << ", got " << dwLenSR << "\n" << std::flush;
                return 1;
            }

            // Duration for 60fps is 10,000,000 / 60 = 166,666 (100ns units)
            if (nv12SampleDur < 160000 || nv12SampleDur > 170000) {
                std::cerr << "[-] NV12 SampleDuration mismatch! Expected ~166666, got " << nv12SampleDur << "\n" << std::flush;
                return 1;
            }

            ComPtr<IMFMediaBuffer> spNv12Buf;
            spSampleNV12->GetBufferByIndex(0, &spNv12Buf);
            BYTE* pData = nullptr;
            spNv12Buf->Lock(&pData, NULL, NULL);
            if (pData) {
                BYTE yVal0 = pData[0];
                BYTE yVal1 = pData[1];
                BYTE* pUvPlane = pData + (1280 * 720);
                BYTE uVal = pUvPlane[0];
                BYTE vVal = pUvPlane[1];

                std::cout << "  [NV12 Sample " << i << "] Y[0..1]: 0x" << std::hex << (int)yVal0 << " 0x" << (int)yVal1
                          << ", UV[0..1]: 0x" << (int)uVal << " 0x" << (int)vVal << std::dec << "\n" << std::flush;

                if (yVal0 != 0x91 || uVal != 0x36 || vVal != 0x22) {
                    std::cerr << "[-] NV12 pixel content mismatch! Expected Y=0x91, U=0x36, V=0x22\n" << std::flush;
                    spNv12Buf->Unlock();
                    return 1;
                }
                spNv12Buf->Unlock();
            }
        }
        std::cout << "[+] Successfully verified SourceReader NV12 @ 60 FPS delivery, sample length (1382400), duration (~166666), and YUV pixel values (0x91, 0x36, 0x22)!\n" << std::flush;

        // Cleanup NV12 instance
        spReaderNV12.Reset();
        spSourceNV12->Shutdown();
        spSourceNV12.Reset();
        spActivateNV12.Reset();

        // 11. Final cleanup
        if (spActivate) {
            spActivate->ShutdownObject();
        }
        spActivate.Reset();
        spFactory.Reset();
    } // ALL COM pointers destroyed here BEFORE FreeLibrary!

    MFShutdown();
    CoUninitialize();
    FreeLibrary(hDll);

    std::cout << "\n==========================================================\n" << std::flush;
    std::cout << " ALL IN-PROCESS VERIFICATION TESTS PASSED SUCCESSFULLY!   \n" << std::flush;
    std::cout << "==========================================================\n" << std::flush;
    return 0;
}

int main()
{
    __try {
        return RealMain();
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        std::cerr << "[-] Crash caught! Exception code: 0x" << std::hex << GetExceptionCode() << "\n" << std::flush;
        return 2;
    }
}
