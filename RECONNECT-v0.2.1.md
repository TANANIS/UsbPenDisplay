# 0.2.1 重連修正與驗證

本輪只實作重連，不改 SVG 面板或完整安裝包。使用者已接受核心與功能。

## 改變的行為

- 啟動時未接線：保持等待，不直接退出；安裝 APK 延後至指定裝置可用。
- 分辨 WaitingForDevice、NeedsUsbAuthorization、DeviceOffline、Connecting、Synchronizing、Ready、Recovering 與需協助狀態。現階段記錄於日誌，後續面板將使用這些狀態。
- 正常連線每兩秒只探測指定裝置的 get-state；恢復時才檢查轉送規則與 App。偵測失聯立即關閉兩條資料通道並釋放筆；ADB 命令有逾時與取消。
- 修復自己的兩個 reverse 規則，必要時重新啟動自己的 App、提供當前 session；沒有全域 adb restart 或另一台裝置操作。
- App 在背景不會被反覆拉回；從桌面重開不會用缺少 extras 的 Intent 覆寫現有 session。App 冷啟動或舊 session 留在前景時，可以由主機恢復當前 session。啟動重試有間隔及次數上限，停止會撤銷重試。
- 影像的第一張完整畫面 ACK 與筆通道均就緒才標示 Ready。斷線時 Android 加遮罩、停用筆輸入，恢復後重新完整同步。
- transport epoch 防止 reset 前的 ACK／輸入工作重新啟用筆。停止會取消 ADB 命令、等待管理迴圈退出後清理規則。

## 找到並修正的根因

完全靜止的桌面沒有新差異可傳，ACK reader 也沒有待確認幀。因此只 Dispose socket 可能無法讓舊的傳送迴圈退出，下一個影像連線被卡在 accept 之前。動畫測試會提供新畫面，反而掩蓋問題。

修正加入每條影像連線的 CancellationTokenSource，reset 明確取消迴圈；無在途 ACK 時每 250 ms 檢查 peer EOF。新增「來源只產生第一幀，其後永遠不更新」的回歸測試，驗證自然 EOF、明確 reset 與兩次後續完整同步。

## 已通過

1. 狀態機測試：未連線／未授權／offline、延後安裝、規則修復、兩通道 gate、通道失效、App 未執行／在背景、transport loss、重試前與修復中取消、不記錄 token。
2. 實際 Engine loopback：原有 window=1／2 精確差異重建、錯誤 ACK 拒絕與重連完整同步，以及新增靜止來源的 EOF／取消回歸。
3. 實機生命週期套件：真實 ADB 對不存在的測試序號啟動會等待且可停止；三星平板移除兩條規則並 force-stop App 後自行恢復；切到 Home 六秒不搶前景，手動重開無 extras 可恢復；重啟主機替換舊 token 並恢復。
4. 最終 0.2.1 建置、APK 簽署／安裝、協定／狀態機／loopback 驗證通過。Canvas 雙幀實機逐像素校驗與故障注入通過，共 544 個已 ACK 且驗證的幀；獨立診斷畫布收到 234 個合成筆事件、壓力 102–1024。

實機生命週期套件在最終版本資訊更新前完成，執行日誌仍印 0.2；同一套重連程式碼隨後由最終 0.2.1 逐幀／故障注入再次驗證。原始紀錄位於 `evidence/v0.2.1/`。早期失敗紀錄保留於工作目錄，未當成通過。

## 仍需區分的範圍

- 實體拔插已由使用者確認「已拔插，畫面自行恢復」。同次 live 紀錄顯示 WaitingForDevice → Connecting → 修復兩條 reverse → StartingApp → Synchronizing → Ready；三星 ADB transport_id 由 60 變為 61，Pixel 維持 49。此項記錄為一次實體拔插成功，證據在 `evidence/v0.2.1/physical-replug/`。
- 持筆接觸時拔線仍未測；一般拔插成功不代表已驗證持續落筆的釋放或長時間／反覆拔插穩定性。不存在的序號測試、force-stop 與刪除規則仍與實體測試分開記錄。
- 斷線偵測是有限延遲：健康時兩秒裝置探測，命令逾時上限四秒；輸入 heartbeat 讀取逾時 4.5 秒，待 ACK 逾時五秒。實際恢復還取決於 OS／ADB／App 啟動及完整畫面同步，不承諾固定秒數。
- 平板鎖屏與 USB 授權仍由使用者處理；本程式不會解鎖或代按系統授權。
- 維持原型依賴與腳本啟動。完整 GUI／系統匣／連線首頁與自足安裝包尚未實作。

重跑：先 Stop，使用 PowerShell 7 執行 `Verify-Reconnect.ps1`；逐幀與故障注入執行 `Verify.ps1 -Recovery`。一般使用維持 `Start.cmd`／`Stop.cmd`。
