(() => {
  const requested = new URLSearchParams(location.search).get('lang') || navigator.language;
  const language = !/^zh/i.test(requested) ? 'en-US' : /(?:HK|TW|Hant)/i.test(requested) ? 'zh-HK' : 'zh-CN';
  const index = language === 'zh-CN' ? 0 : 1;
  const messages = {
  "iPhoneMirror SRS Test": [
    "iPhoneMirror SRS 测试",
    "iPhoneMirror SRS 測試"
  ],
  "iPhoneMirror Stream Lab": [
    "iPhoneMirror 推流实验室",
    "iPhoneMirror 串流實驗室"
  ],
  "LOCAL TEST BENCH": [
    "本地测试台",
    "本機測試台"
  ],
  "PUBLISH TARGETS": [
    "推流目标",
    "串流目標"
  ],
  "Checking media server": [
    "正在检查媒体服务器",
    "正在檢查媒體伺服器"
  ],
  "Refresh": [
    "刷新",
    "重新整理"
  ],
  "Use the same stream name in the app": [
    "使用与应用相同的流名称",
    "使用與應用程式相同的串流名稱"
  ],
  "Open backend player": [
    "打开后端播放器",
    "開啟後端播放器"
  ],
  "Protocol": [
    "协议",
    "協定"
  ],
  "Endpoint": [
    "地址",
    "位址"
  ],
  "Role": [
    "用途",
    "用途"
  ],
  "Publish from iPhoneMirror": [
    "从 iPhoneMirror 推流",
    "從 iPhoneMirror 發佈串流"
  ],
  "Browser playback": [
    "浏览器播放",
    "瀏覽器播放"
  ],
  "WEBRTC PLAYBACK": [
    "WebRTC 播放",
    "WebRTC 播放"
  ],
  "WHEP monitor": [
    "WHEP 预览",
    "WHEP 預覽"
  ],
  "Start monitor": [
    "开始预览",
    "開始預覽"
  ],
  "Stop": [
    "停止",
    "停止"
  ],
  "Waiting for a live stream": [
    "等待实时流",
    "等待即時串流"
  ],
  "VIRTUAL CAMERA": [
    "虚拟摄像头",
    "虛擬攝影機"
  ],
  "Browser device check": [
    "浏览器设备检查",
    "瀏覽器裝置檢查"
  ],
  "Scan cameras": [
    "扫描摄像头",
    "掃描攝影機"
  ],
  "Video input": [
    "视频输入",
    "視訊輸入"
  ],
  "Request camera access to list devices": [
    "允许摄像头访问以列出设备",
    "允許存取攝影機以列出裝置"
  ],
  "Open camera": [
    "打开摄像头",
    "開啟攝影機"
  ],
  "Device": [
    "设备",
    "裝置"
  ],
  "Not open": [
    "未打开",
    "未開啟"
  ],
  "Resolution": [
    "分辨率",
    "解像度"
  ],
  "Actual FPS": [
    "实际帧率",
    "實際影格率"
  ],
  "Longest frame gap": [
    "最长帧间隔",
    "最長影格間隔"
  ],
  "Near-black frames": [
    "近黑帧数",
    "接近全黑的影格數"
  ],
  "Camera is not open.": [
    "摄像头未打开。",
    "攝影機未開啟。"
  ],
  "SERVER INVENTORY": [
    "服务器状态",
    "伺服器狀態"
  ],
  "Active media streams": [
    "活动媒体流",
    "使用中的媒體串流"
  ],
  "0 streams": [
    "流数量：0",
    "串流數量：0"
  ],
  "Waiting for media server.": [
    "等待媒体服务器。",
    "等待媒體伺服器。"
  ],
  "No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror.": [
    "没有活动流。请从 iPhoneMirror 启动 RTMP、SRT 或 WHIP 推流。",
    "沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。"
  ],
  "Media server unavailable": [
    "媒体服务器不可用",
    "媒體伺服器無法使用"
  ],
  "No response from media server": [
    "媒体服务器没有响应",
    "媒體伺服器沒有回應"
  ],
  "Media server ready": [
    "媒体服务器已就绪",
    "媒體伺服器已就緒"
  ],
  "Dashboard request failed": [
    "状态请求失败",
    "狀態請求失敗"
  ],
  "Negotiating WHEP playback": [
    "正在建立 WHEP 播放连接",
    "正在建立 WHEP 播放連線"
  ],
  "WebRTC connection failed": [
    "WebRTC 连接失败",
    "WebRTC 連線失敗"
  ],
  "Waiting for video frames": [
    "等待视频帧",
    "等待視訊影格"
  ],
  "No video input found": [
    "未找到视频输入",
    "找不到視訊輸入"
  ],
  "No video input was found.": [
    "未找到视频输入。",
    "找不到視訊輸入。"
  ],
  "Camera APIs are unavailable in this browser context.": [
    "当前浏览器环境不支持摄像头接口。",
    "目前瀏覽器環境不支援攝影機介面。"
  ],
  "Camera permission request timed out.": [
    "摄像头权限请求超时。",
    "攝影機權限請求逾時。"
  ],
  "Camera permission request timed out. Allow camera access and scan again.": [
    "摄像头权限请求超时。请允许访问后重新扫描。",
    "攝影機權限請求逾時。請允許存取後重新掃描。"
  ],
  "No video input is available.": [
    "没有可用的视频输入。",
    "沒有可用的視訊輸入。"
  ],
  "Camera is delivering frames.": [
    "摄像头正在传输画面。",
    "攝影機正在傳送畫面。"
  ],
  "WHEP Playback": [
    "WHEP 播放",
    "WHEP 播放"
  ],
  "Start WHEP playback": [
    "开始 WHEP 播放",
    "開始 WHEP 播放"
  ],
  "Stop playback": [
    "停止播放",
    "停止播放"
  ],
  "Idle.": [
    "空闲。",
    "閒置。"
  ],
  "Endpoints": [
    "连接地址",
    "連線位址"
  ],
  "App": [
    "应用名",
    "應用程式名稱"
  ],
  "Stream": [
    "流名称",
    "串流名稱"
  ],
  "Publish browser camera through WHIP": [
    "通过 WHIP 推送浏览器摄像头画面",
    "透過 WHIP 發佈瀏覽器攝影機畫面"
  ],
  "Stop browser publish": [
    "停止浏览器推流",
    "停止瀏覽器串流"
  ],
  "Connecting to WHEP...": [
    "正在连接 WHEP…",
    "正在連線至 WHEP…"
  ],
  "Connected.": [
    "已连接。",
    "已連線。"
  ],
  "Requesting browser camera...": [
    "正在请求摄像头权限…",
    "正在請求攝影機權限…"
  ],
  "Browser camera is idle.": [
    "浏览器摄像头空闲。",
    "瀏覽器攝影機閒置。"
  ],
  "Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP.": [
    "通过 iPhoneMirror 以 RTMP、SRT 或 WHIP 推流，再通过 WHEP 验证同一条流。",
    "透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發佈串流，再透過 WHEP 驗證同一串流。"
  ],
  "The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check.": [
    "此页面用于验证视频传输。iPhoneMirror 可在音源可用时包含音频；浏览器摄像头推流是独立的 SRS 测试。",
    "此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。"
  ],
  "Media server publish and playback endpoints": [
    "媒体服务器推流与播放地址",
    "媒體伺服器串流與播放位址"
  ],
  "Streams: {0}": [
    "流数量：{0}",
    "串流數量：{0}"
  ],
  "Clients: {0}": [
    "客户端：{0}",
    "用戶端：{0}"
  ],
  "Camera {0}": [
    "摄像头 {0}",
    "攝影機 {0}"
  ],
  "Video inputs: {0}. Selected: {1}.": [
    "视频输入：{0}。已选择：{1}。",
    "視訊輸入：{0}。已選擇：{1}。"
  ],
  "Frame analysis failed: {0}": [
    "帧分析失败：{0}",
    "影格分析失敗：{0}"
  ],
  "Request returned HTTP {0}": [
    "请求返回 HTTP {0}",
    "請求傳回 HTTP {0}"
  ],
  "Playback state: {0}": [
    "播放状态：{0}",
    "播放狀態：{0}"
  ],
  "Publish state: {0}": [
    "推流状态：{0}",
    "串流狀態：{0}"
  ],
  "failed": [
    "失败",
    "失敗"
  ],
  "disconnected": [
    "已断开",
    "已中斷"
  ],
  "closed": [
    "已关闭",
    "已關閉"
  ]
};
  window.uiText = (key, ...values) => {
    const message = language === 'en-US' ? key : messages[key]?.[index] ?? key;
    return message.replace(/\{(\d+)\}/g, (_, i) => String(values[Number(i)] ?? ''));
  };
  document.documentElement.lang = language;
  document.querySelectorAll('[data-i18n]').forEach(element => { element.textContent = uiText(element.textContent.trim()); });
  document.querySelectorAll('[data-i18n-label]').forEach(element => { element.setAttribute('aria-label', uiText(element.getAttribute('aria-label'))); });
})();
