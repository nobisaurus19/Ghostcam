# Ghostcam Studio 👻

> **Turn your iPhone or Android into a 60 FPS 4K Wireless Webcam & Windows Virtual Camera — No Phone App Required, Zero Watermarks.**  
> **เปลี่ยนมือถือ iPhone / Android ให้เป็นกล้องเว็บแคมไร้สายระดับ 4K 60 FPS พร้อม Virtual Camera บน Windows 10/11 — ไม่ต้องลงแอปในมือถือ ไม่มีลายน้ำ**

[![Download Portable EXE](https://img.shields.io/badge/Download-Ghostcam--Portable.exe-00B995?style=for-the-badge&logo=github)](https://github.com/nobisaurus19/Ghostcam/releases/latest)
[![Download ZIP](https://img.shields.io/badge/Zip-Ghostcam--win--x64.zip-1F2937?style=for-the-badge&logo=github)](https://github.com/nobisaurus19/Ghostcam/releases/latest)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-111111?style=for-the-badge&logo=windows)](https://github.com/nobisaurus19/Ghostcam/releases/latest)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0--windows-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)

---

## 📦 Direct Download (สำหรับผู้ใช้งานทั่วไป)

1. **Option 1 (Recommended): [`Ghostcam-Portable.exe` (1-Click Single File Portable)](https://github.com/nobisaurus19/Ghostcam/releases/latest)**
   - **ไฟล์เดียวจบ ไม่ต้องแตกไฟล์ Zip ไม่ต้องลง .NET เปิดใช้งานได้ทันที!**
   - Self-contained single-file executable for Windows 10 / 11 (64-bit) — automatically extracts the embedded `GhostCamVirtualCamera.dll` and one-click registration helper on launch.

2. **Option 2: [`Ghostcam-win-x64.zip` (Portable Folder)](https://github.com/nobisaurus19/Ghostcam/releases/latest)**
   - **Self-Contained Portable Package** for Windows 10 / 11 (64-bit).
   - **No .NET SDK or runtime installation required** — just unzip and run `Ghostcam.exe`.
   - Includes the prebuilt native Windows Virtual Camera (`GhostCamVirtualCamera.dll`) and one-click registration helper.

---

## ✨ Key Features / ความสามารถหลัก

- **📱 60 FPS / 30 FPS Wireless WebCam via Safari & Chrome (ไม่ต้องลงแอปในมือถือ)**
  - Scan the QR code directly with **Safari (iPhone)** or **Chrome (Android)** over local Wi-Fi (port `9443` HTTPS / `9442` HTTP with WebSocket binary streaming).
  - Supports **WebCodecs Hardware H.264 (`H264` toggle)** and **JPEG** streaming across **4K UHD (`3840x2160`)**, **Full HD (`1920x1080`)**, and **HD (`1280x720`)** at **60 FPS** or **30 FPS** — all unlocked with zero watermarks.
  - Also includes a built-in **60 FPS Synthetic Test Camera** and **USB Tunnel** support (`usbmuxd` for iOS / `adb` for Android).

- **🎙️ Wireless Mobile Microphone & 1-Bit Pixel VU Meter**
  - Stream microphone audio directly from your phone (`🎙️ ไมค์`) over WebSocket (`24 kHz` 16-bit PCM) with optional PC **MIC** audio monitoring and a live **5-bar 1-bit pixel VU meter**.

- **🎥 Native Windows 11 Media Foundation Virtual Camera**
  - Outputs directly as **`Ghost Cam (Windows Virtual Camera)`** (`GhostCamVirtualCamera.dll` / `GhostCam Virtual Camera`) supporting both **NV12** and **YUY2** at **60 FPS** and **30 FPS**.
  - Works seamlessly across **Discord**, **OBS Studio**, **Zoom**, **Microsoft Teams**, and **Google Meet** via shared memory (`C:\ProgramData\Ghostcam\frame.shm`) and low-latency event sync.

- **❄️ iPhone Thermal Cool Mode & Smart Multi-Lens + 2x Crop Fallback**
  - **`❄️ ลดร้อน: ON`** thermal cool mode reduces phone heat during long streams, while **OLED Blackout (`🌙 พักหน้าจอ`)** blacks out the phone screen without stopping video transmission.
  - **Smart Multi-Lens Switcher**: **`0.5x`**, **`1x`**, **`2x`**, **`Selfie`** with physical camera detection and automatic **2x center sensor crop fallback** for single-lens phones.

- **🎛️ Real-Time AI & ISP Studio Controls + Recorder + Profiles**
  - **AI & Portrait FX**: **Auto Face Framing (Center Stage)**, **Portrait Background Blur (`Bokeh`)**, and **Green Screen (`Chroma Key`)**.
  - **LUTs & `.cube` Import**: 6 built-in presets (`NATURAL`, `STUDIO WARM`, `TEAL & ORANGE`, `CYBERPUNK`, `NOIR MONO`, `VIVID POP`) plus **`IMPORT .CUBE LUT`** for custom 3D LUTs.
  - **Hardware Exposure & Fine Adjustments**: `Sensor ISO`, `Shutter Speed`, `White Balance (Kelvin)`, `Manual Focus`, `Zoom`/`Pan`/`Mirror`/`Rotate 90°`/`HUD`, plus `Brightness`, `Contrast`, `Saturation`, `Temperature`, `Tint`, `Sharpness`, and `Vignette`.
  - **Capture & Workflow**: Instant PNG **`SNAPSHOT`**, built-in **`● REC`** MJPEG `.avi` video recorder (`Pictures\Ghostcam`), automatic settings persistence with **`P1 / P2 / P3` custom profiles**, and **Minimize to System Tray (`TRAY`)**.

- **🖤🤍 Monochrome Light / Dark 8px Grid UI (YOYU Inspired)**
  - Clean architectural 8px-grid interface with instant **Light / Dark Theme** toggle, crisp telemetry strip, and custom 1-bit pixel-art ghost animations.

---

## 🚀 Quick Start (For Users / วิธีใช้งานเบื้องต้น)

1. **Download**
   - Download [`Ghostcam-Portable.exe`](https://github.com/nobisaurus19/Ghostcam/releases/latest) (single-file portable, no extraction needed) — or download [`Ghostcam-win-x64.zip`](https://github.com/nobisaurus19/Ghostcam/releases/latest) and extract it to any folder (e.g., `C:\Tools\Ghostcam`).
2. **Launch Ghostcam Studio**
   - Run `Ghostcam-Portable.exe` (or `Ghostcam.exe` from the Zip package, or `Run-Ghostcam.bat` when running from source).
3. **Connect Your Phone (Wireless QR)**
   - Ensure your PC and phone are on the **same Wi-Fi network**.
   - Select **MOBILE WEBCAM (Safari / QR)** and scan the QR code with your iPhone (Safari) or Android (Chrome).
   - *Tip for iPhone Safari*: If a local self-signed HTTPS certificate warning appears, tap **Show Details → Visit this website** to allow local LAN camera streaming.
4. **Start Virtual Camera**
   - Click **START VIRTUAL CAMERA** in Ghostcam Studio (accept the UAC prompt on first run if prompted to register `GhostCamVirtualCamera.dll`, or run `scripts/Register-GhostCam.bat` as Administrator).
   - Select **GhostCam Virtual Camera** in **Discord**, **OBS Studio**, **Zoom**, **Microsoft Teams**, or **Google Meet**.

---

## 🛠️ Build From Source (For Developers)

### Prerequisites
- Windows 10 (Build 19041+) or Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- *(Optional, only if rebuilding the native C++ Virtual Camera DLL)* Visual Studio 2022 C++ Desktop workload (`src/GhostCamVirtualCamera/Build-GhostCamDll.ps1`)

### Build & Run

```powershell
# Clone the repository
git clone https://github.com/nobisaurus19/Ghostcam.git
cd Ghostcam

# Build the solution in Release mode
dotnet build Ghostcam.sln -c Release

# Run Ghostcam Studio
dotnet run --project src/Ghostcam.Studio/Ghostcam.Studio.csproj -c Release
```

### Repository Layout

```text
Ghostcam/
├── Ghostcam.sln                        # .NET 8 solution
├── Ghostcam.exe                        # Launcher binary
├── Run-Ghostcam.bat                    # Quick launch script for Release build
├── lib/                                # Managed dependencies (QRCoder, Makaretu.Dns, BouncyCastle)
├── scripts/                            # Virtual Camera COM registration scripts (Register/Unregister)
└── src/
    ├── Ghostcam.Core/                  # WebCam HTTPS/HTTP/WebSocket server, QR pairing, USB bridge
    ├── Ghostcam.Media/                 # 60 FPS pixel/LUT processor, H.264 parser, Virtual Camera IPC
    ├── Ghostcam.Studio/                # WPF Desktop UI (YOYU Light/Dark 8px grid, pixel-art animations)
    └── GhostCamVirtualCamera/          # Native C++ Media Foundation Virtual Camera COM source (NV12/YUY2)
```

---

## 🔒 Privacy & Local Operation

- **100% Local**: Video frames never leave your local network — no cloud servers, no accounts, and no telemetry.
- Local Wi-Fi streaming stays strictly between your phone browser and Ghostcam Studio on your PC.

---

## 📄 License

Maintained by [@nobisaurus19](https://github.com/nobisaurus19).  
All third-party library and product names remain the property of their respective owners.
