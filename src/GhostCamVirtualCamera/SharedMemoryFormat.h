#pragma once

#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <sddl.h>
#include <mutex>

// {8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}
inline constexpr GUID CLSID_GhostCamActivate =
    { 0x8f4c2a19, 0x7b3e, 0x4d12, { 0x9a, 0x88, 0x1e, 0x6f, 0x3c, 0x5a, 0x9b, 0x01 } };

inline constexpr wchar_t GHOSTCAM_CLSID_STRING[]   = L"{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}";
inline constexpr wchar_t GHOSTCAM_FRIENDLY_NAME[]  = L"GhostCam Virtual Camera";
inline constexpr wchar_t GHOSTCAM_SHM_FILE_PATH[]  = L"C:\\ProgramData\\Ghostcam\\frame.shm";
inline constexpr wchar_t GHOSTCAM_SHM_DIR[]        = L"C:\\ProgramData\\Ghostcam";
inline constexpr wchar_t GHOSTCAM_EVENT_NAME[]     = L"Global\\GhostCam_FrameSync";
inline constexpr wchar_t GHOSTCAM_SDDL[]           = L"D:(A;;GA;;;WD)(A;;GA;;;AC)";

inline constexpr uint32_t GHOSTCAM_SHM_SIZE        = 18 * 1024 * 1024; // 18,874,368 bytes

inline constexpr uint32_t GHOSTCAM_MAGIC_GHST      = 0x47485354; // 'GHST'
inline constexpr uint32_t GHOSTCAM_VERSION_1       = 1;
inline constexpr uint32_t GHOSTCAM_FORMAT_YUY2     = 1;
inline constexpr uint32_t GHOSTCAM_FLAG_ACTIVE     = 0x00000001;

#pragma pack(push, 1)
struct GhostCamShmHeader {
    uint32_t       Magic;           // 0x47485354 ('GHST')
    uint32_t       Version;         // 1
    volatile LONG  FrameSeq;        // Monotonically incremented per frame
    uint32_t       Width;           // 1280, 1920, 3840
    uint32_t       Height;          // 720, 1080, 2160
    uint32_t       Fps;             // 30
    uint32_t       Format;          // 1 = YUY2
    uint32_t       PayloadBytes;    // Width * Height * 2
    uint64_t       TimestampTicks;  // Monotonic ticks / 100ns units
    uint32_t       Flags;           // Bit 0 = Active streaming
    uint8_t        Reserved[20];    // Total 64 bytes
};
#pragma pack(pop)

static_assert(sizeof(GhostCamShmHeader) == 64, "GhostCamShmHeader must be exactly 64 bytes");

inline bool CreateGhostCamSecurityAttributes(SECURITY_ATTRIBUTES* pSa, PSECURITY_DESCRIPTOR* ppSd)
{
    if (!pSa || !ppSd) return false;
    *ppSd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            GHOSTCAM_SDDL,
            SDDL_REVISION_1,
            ppSd,
            NULL))
    {
        pSa->nLength = sizeof(SECURITY_ATTRIBUTES);
        pSa->lpSecurityDescriptor = *ppSd;
        pSa->bInheritHandle = FALSE;
        return true;
    }
    return false;
}

inline void FreeGhostCamSecurityAttributes(PSECURITY_DESCRIPTOR pSd)
{
    if (pSd) {
        LocalFree(pSd);
    }
}

inline void LogVcamDebug(const char* fmt, ...)
{
    static std::mutex s_logMutex;
    std::lock_guard<std::mutex> lock(s_logMutex);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char prefix[128];
    snprintf(prefix, sizeof(prefix), "[%04d-%02d-%02d %02d:%02d:%02d.%03d][PID:%lu][TID:%lu] ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
             GetCurrentProcessId(), GetCurrentThreadId());

    char message[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(message, sizeof(message), fmt, args);
    va_end(args);

    CreateDirectoryW(L"C:\\ProgramData", NULL);
    CreateDirectoryW(GHOSTCAM_SHM_DIR, NULL);

    SECURITY_ATTRIBUTES sa = {};
    PSECURITY_DESCRIPTOR pSd = nullptr;
    SECURITY_ATTRIBUTES* pSaPtr = nullptr;
    if (CreateGhostCamSecurityAttributes(&sa, &pSd)) {
        pSaPtr = &sa;
    }

    HANDLE hFile = CreateFileW(
        L"C:\\ProgramData\\Ghostcam\\vcam_debug.log",
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        pSaPtr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(hFile, prefix, static_cast<DWORD>(strlen(prefix)), &written, NULL);
        WriteFile(hFile, message, static_cast<DWORD>(strlen(message)), &written, NULL);
        WriteFile(hFile, "\r\n", 2, &written, NULL);
        CloseHandle(hFile);
    }

    FreeGhostCamSecurityAttributes(pSd);
}
