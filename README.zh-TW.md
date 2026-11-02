# USB Pen Display

以一條 USB 將 Windows 完整主螢幕映射到 Android 平板，並將 S Pen 座標、筆壓、傾斜、側鍵與橡皮擦資訊傳回 Windows。主要測試裝置為 Galaxy Tab S6 Lite SM-P610（LineageOS／Android 16），電腦端為 Windows x64。

這是第一個可下載版本 **v0.3.0**。核心映射與筆功能、一次實體 USB 拔插恢復已由使用者確認；尚無與專業繪圖螢幕的 pen-to-photon 實測對照。

## 下載與使用

請從 [GitHub Releases](https://github.com/TANANIS/UsbPenDisplay/releases) 下載：

- `UsbPenDisplay-0.3.0-win-x64.zip`：完整 Windows 可攜套件，內含 EXE、.NET 執行環境、USB 工具與 APK。
- `UsbPenDisplay-0.3.0-arm64.apk`：Android ARM64 安裝檔，可獨立安裝。
- `SHA256SUMS.txt`：下載檔案 SHA-256。

1. 解壓 Windows ZIP，保留 EXE、`tools/` 與 APK 的相對位置。
2. 接上可傳資料的 USB 線，解鎖平板，啟用 USB 偵錯，並由本人允許電腦連線。
3. 開啟 `UsbPenDisplay.exe`，選擇平板、畫質，按「開始繪圖連線」。沒有裝置時面板會持續等候。
4. 程式會在需要時安裝／更新同套件的 APK，並開啟平板 App。已安裝相同版本時跳過更新。
5. 在電腦開啟 CSP／Aseprite，用平板 S Pen 操作。按「停止」結束；關閉面板會收至系統匣，請從系統匣「結束程式」退出。

不需要另外安裝 .NET、Android SDK 或 PowerShell。Windows 可攜套件不是安裝精靈；移除前先結束程式，再刪除解壓資料夾。使用者設定與紀錄在 `%LOCALAPPDATA%\UsbPenDisplay`。

目前 EXE 未做 Windows 程式碼簽章；APK 使用本機原型簽章，金鑰不隨原始碼／下載檔散布。更換 APK 簽章將無法直接覆蓋既有安裝。

## 功能與連線恢復

- 完整主螢幕等比例映射，不裁切畫布、不建立第二桌面。
- GPU 縮圖與精確變動區塊讀回；64×64 差異區塊、Raw／LZ4 傳輸。
- 有界一／兩幀管線，明確差異基底與重新連線完整同步。
- Android 原生解碼、Canvas／OpenGL 呈現。預設 GPU 擷取＋Canvas＋雙幀。
- 獨立筆通道與 Windows Pointer API 筆輸入。
- 斷線釋放筆、平板遮罩與輸入暫停；接回後修復自己的 USB 轉送規則，完整畫面確認及筆通道就緒後恢復。
- App 關閉、電腦程序重啟、背景切換與重新開啟的恢復；不重啟全域 ADB。
- 電腦端基本裝置／畫質／開始停止面板、系統匣與紀錄入口；Android 為全螢幕映射及連線提示。

USB 授權、解鎖需使用者處理。按住實體筆時拔線、長時間反覆拔插及廣泛裝置相容性尚未完成驗證。完整 SVG 面板、Android 獨立設定頁及安裝精靈仍屬後續工作。

CSP 可先選 TabletPC 平板服務；Aseprite 可先選 Windows 8/10 Pointer API，再依筆刷設定 Dynamics。本程式不改動繪圖軟體設定。手指觸控手勢尚未實作，縮放、平移、旋轉使用繪圖軟體原有操作。

## 開發與驗證

需求：Windows x64、.NET 8 SDK、JDK 17、Android SDK platform 35／build-tools 35.0.1／NDK 28.1.13356709。`Build.ps1` 的 `-JavaHome`／`-AndroidSdk` 可指定工具位置；套件產生與驗證腳本需要 PowerShell 7。

```powershell
.\Build.ps1 -JavaHome "$env:JAVA_HOME"
.\Package.ps1
.\Verify.ps1 -Serial DEVICE_SERIAL -Recovery
.\Verify-Reconnect.ps1 -Serial DEVICE_SERIAL
```

在目前安裝 APK 的同一台開發機重建時，保留 `build/prototype.keystore` 以維持原型簽章；請自行備份，不要提交到版本控制。全新開發環境會產生自己的原型金鑰。

`Package.ps1` 產生自足的 Windows x64 單檔 EXE，並搭配 ADB 工具、APK 及第三方授權組成可攜 ZIP；目前指定 .NET 8.0.30 runtime。EXE 本身是應用程式，不是安裝器，完整使用需保留 ZIP 內配套檔案。

協定／狀態機與引擎測試：

```powershell
.\release\host\UsbPenDisplay.exe --verify
.\release\host\UsbPenDisplay.exe --verify-reconnect
.\release\host\UsbPenDisplay.exe --verify-pipeline --logs .\logs\loopback
```

`Verify-Reconnect` 會暫時關閉自己的平板 App、移除自己的兩條轉送規則、切到 Home 後重開；執行前先停止映射。它不模擬硬體拔插。`Verify -Recovery` 包含實機逐像素與重新完整同步驗證。原始本機紀錄包含裝置識別資訊，不提交至公開倉庫。

## 計時與限制

固定合成負載的 Canvas 雙幀更新到 ACK 中位數：小筆跡約 27.6 ms，大面積旋轉縮放約 44.4 ms／31.8 次更新每秒。僅一次負載比較，包含合成畫面產生與回程 ACK，排除 Windows 桌面擷取、實體筆採樣與繪圖軟體繪製。**不是筆尖到面板發光延遲**，不能據此宣稱等同繪王等繪圖螢幕。

目前不支援多螢幕選擇／主螢幕熱切換、HDR 色彩管理、手指多點手勢、AOA 插線即用或無線。主螢幕幾何改變時請停止後重開連線。沒有本地預測筆跡；平板只有筆位置圈。

技術文件：[協定](PROTOCOL-v2.md)、[重連驗證](RECONNECT-v0.2.1.md)、[效能觀察](RESULTS-v0.2.md)、[SVG 介面提案](design/v0.3-panel.svg)。第三方元件與授權見 [THIRD_PARTY.md](THIRD_PARTY.md)；本專案尚未指定原創程式碼的再利用授權。
