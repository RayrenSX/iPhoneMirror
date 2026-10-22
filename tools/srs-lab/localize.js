(() => {
  const requested = new URLSearchParams(location.search).get('lang') || navigator.language;
  const language = /^ja/i.test(requested) ? 'ja-JP' : !/^zh/i.test(requested) ? 'en-US' : /^zh-(?:TW|Hant-TW)$/i.test(requested) ? 'zh-TW' : /(?:HK|MO|Hant|CHT)/i.test(requested) ? 'zh-HK' : 'zh-CN';
  const index = language === 'zh-CN' ? 0 : language === 'zh-TW' ? 2 : language === 'ja-JP' ? 3 : 1;
  const messages = {
  "iPhoneMirror SRS Test": [
    "iPhoneMirror SRS 测试",
    "iPhoneMirror SRS 測試",
    "iPhoneMirror SRS 測試",
    "iPhoneMirror SRSテスト"
  ],
  "iPhoneMirror Stream Lab": [
    "iPhoneMirror 推流实验室",
    "iPhoneMirror 串流實驗室",
    "iPhoneMirror 串流實驗室",
    "iPhoneMirror配信ラボ"
  ],
  "LOCAL TEST BENCH": [
    "本地测试台",
    "本機測試台",
    "本機測試台",
    "ローカルのテスト台"
  ],
  "PUBLISH TARGETS": [
    "推流目标",
    "串流目標",
    "串流目標",
    "配信先"
  ],
  "Checking media server": [
    "正在检查媒体服务器",
    "正在檢查媒體伺服器",
    "正在檢查媒體伺服器",
    "メディアサーバーを確認しています"
  ],
  "Refresh": [
    "刷新",
    "重新整理",
    "重新整理",
    "更新"
  ],
  "Use the same stream name in the app": [
    "使用与应用相同的流名称",
    "使用與應用程式相同的串流名稱",
    "使用與應用程式相同的串流名稱",
    "アプリでも同じストリーム名を使ってください"
  ],
  "Open backend player": [
    "打开后端播放器",
    "開啟後端播放器",
    "開啟後端播放器",
    "サーバー側のプレーヤーを開く"
  ],
  "Protocol": [
    "协议",
    "協定",
    "協定",
    "プロトコル"
  ],
  "Endpoint": [
    "地址",
    "位址",
    "位址",
    "アドレス"
  ],
  "Role": [
    "用途",
    "用途",
    "用途",
    "用途"
  ],
  "Publish from iPhoneMirror": [
    "从 iPhoneMirror 推流",
    "從 iPhoneMirror 發佈串流",
    "從 iPhoneMirror 發布串流",
    "iPhoneMirrorから配信"
  ],
  "Browser playback": [
    "浏览器播放",
    "瀏覽器播放",
    "瀏覽器播放",
    "ブラウザーで再生"
  ],
  "WEBRTC PLAYBACK": [
    "WebRTC 播放",
    "WebRTC 播放",
    "WebRTC 播放",
    "WebRTCの再生"
  ],
  "WHEP monitor": [
    "WHEP 预览",
    "WHEP 預覽",
    "WHEP 預覽",
    "WHEPのモニター"
  ],
  "Start monitor": [
    "开始预览",
    "開始預覽",
    "開始預覽",
    "モニターを開始"
  ],
  "Stop": [
    "停止",
    "停止",
    "停止",
    "停止"
  ],
  "Waiting for a live stream": [
    "等待实时流",
    "等待即時串流",
    "等待即時串流",
    "ライブストリームを待っています"
  ],
  "VIRTUAL CAMERA": [
    "虚拟摄像头",
    "虛擬攝影機",
    "虛擬攝影機",
    "仮想カメラ"
  ],
  "Browser device check": [
    "浏览器设备检查",
    "瀏覽器裝置檢查",
    "瀏覽器裝置檢查",
    "ブラウザーでのデバイスの確認"
  ],
  "Scan cameras": [
    "扫描摄像头",
    "掃描攝影機",
    "掃描攝影機",
    "カメラをスキャン"
  ],
  "Video input": [
    "视频输入",
    "視訊輸入",
    "視訊輸入",
    "ビデオ入力"
  ],
  "Request camera access to list devices": [
    "允许摄像头访问以列出设备",
    "允許存取攝影機以列出裝置",
    "允許存取攝影機以列出裝置",
    "デバイスを一覧にするには、カメラへのアクセスを許可してください"
  ],
  "Open camera": [
    "打开摄像头",
    "開啟攝影機",
    "開啟攝影機",
    "カメラを開く"
  ],
  "Device": [
    "设备",
    "裝置",
    "裝置",
    "デバイス"
  ],
  "Not open": [
    "未打开",
    "未開啟",
    "未開啟",
    "開いていません"
  ],
  "Resolution": [
    "分辨率",
    "解像度",
    "解析度",
    "解像度"
  ],
  "Actual FPS": [
    "实际帧率",
    "實際影格率",
    "實際影格率",
    "実際のfps"
  ],
  "Longest frame gap": [
    "最长帧间隔",
    "最長影格間隔",
    "最長影格間隔",
    "フレームの最大の間隔"
  ],
  "Near-black frames": [
    "近黑帧数",
    "接近全黑的影格數",
    "接近全黑的影格數",
    "ほぼ黒のフレーム"
  ],
  "Camera is not open.": [
    "摄像头未打开。",
    "攝影機未開啟。",
    "攝影機未開啟。",
    "カメラが開いていません。"
  ],
  "SERVER INVENTORY": [
    "服务器状态",
    "伺服器狀態",
    "伺服器狀態",
    "サーバーの状態"
  ],
  "Active media streams": [
    "活动媒体流",
    "使用中的媒體串流",
    "使用中的媒體串流",
    "配信中のストリーム"
  ],
  "0 streams": [
    "流数量：0",
    "串流數量：0",
    "串流數量：0",
    "ストリーム: 0"
  ],
  "Waiting for media server.": [
    "等待媒体服务器。",
    "等待媒體伺服器。",
    "等待媒體伺服器。",
    "メディアサーバーを待っています。"
  ],
  "No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror.": [
    "没有活动流。请从 iPhoneMirror 启动 RTMP、SRT 或 WHIP 推流。",
    "沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。",
    "沒有使用中的串流。請從 iPhoneMirror 啟動 RTMP、SRT 或 WHIP 串流。",
    "配信中のストリームはありません。iPhoneMirrorからRTMP・SRT・WHIPで配信を始めてください。"
  ],
  "Media server unavailable": [
    "媒体服务器不可用",
    "媒體伺服器無法使用",
    "媒體伺服器無法使用",
    "メディアサーバーを使えません"
  ],
  "No response from media server": [
    "媒体服务器没有响应",
    "媒體伺服器沒有回應",
    "媒體伺服器沒有回應",
    "メディアサーバーから応答がありません"
  ],
  "Media server ready": [
    "媒体服务器已就绪",
    "媒體伺服器已就緒",
    "媒體伺服器已就緒",
    "メディアサーバーの準備ができました"
  ],
  "Dashboard request failed": [
    "状态请求失败",
    "狀態請求失敗",
    "狀態要求失敗",
    "状態の取得に失敗しました"
  ],
  "Negotiating WHEP playback": [
    "正在建立 WHEP 播放连接",
    "正在建立 WHEP 播放連線",
    "正在建立 WHEP 播放連線",
    "WHEPの再生の接続を確立しています"
  ],
  "WebRTC connection failed": [
    "WebRTC 连接失败",
    "WebRTC 連線失敗",
    "WebRTC 連線失敗",
    "WebRTCの接続に失敗しました"
  ],
  "Waiting for video frames": [
    "等待视频帧",
    "等待視訊影格",
    "等待視訊影格",
    "ビデオのフレームを待っています"
  ],
  "No video input found": [
    "未找到视频输入",
    "找不到視訊輸入",
    "找不到視訊輸入",
    "ビデオ入力が見つかりません"
  ],
  "No video input was found.": [
    "未找到视频输入。",
    "找不到視訊輸入。",
    "找不到視訊輸入。",
    "ビデオ入力が見つかりませんでした。"
  ],
  "Camera APIs are unavailable in this browser context.": [
    "当前浏览器环境不支持摄像头接口。",
    "目前瀏覽器環境不支援攝影機介面。",
    "目前瀏覽器環境不支援攝影機介面。",
    "このブラウザーの環境では、カメラのAPIを使えません。"
  ],
  "Camera permission request timed out.": [
    "摄像头权限请求超时。",
    "攝影機權限請求逾時。",
    "攝影機權限要求逾時。",
    "カメラの許可の要求がタイムアウトしました。"
  ],
  "Camera permission request timed out. Allow camera access and scan again.": [
    "摄像头权限请求超时。请允许访问后重新扫描。",
    "攝影機權限請求逾時。請允許存取後重新掃描。",
    "攝影機權限要求逾時。請允許存取後重新掃描。",
    "カメラの許可の要求がタイムアウトしました。カメラへのアクセスを許可して、もう一度スキャンしてください。"
  ],
  "No video input is available.": [
    "没有可用的视频输入。",
    "沒有可用的視訊輸入。",
    "沒有可用的視訊輸入。",
    "使えるビデオ入力がありません。"
  ],
  "Camera is delivering frames.": [
    "摄像头正在传输画面。",
    "攝影機正在傳送畫面。",
    "攝影機正在傳送畫面。",
    "カメラから映像が届いています。"
  ],
  "WHEP Playback": [
    "WHEP 播放",
    "WHEP 播放",
    "WHEP 播放",
    "WHEPの再生"
  ],
  "Start WHEP playback": [
    "开始 WHEP 播放",
    "開始 WHEP 播放",
    "開始 WHEP 播放",
    "WHEPの再生を開始"
  ],
  "Stop playback": [
    "停止播放",
    "停止播放",
    "停止播放",
    "再生を停止"
  ],
  "Idle.": [
    "空闲。",
    "閒置。",
    "閒置。",
    "待機中。"
  ],
  "Endpoints": [
    "连接地址",
    "連線位址",
    "連線位址",
    "接続先"
  ],
  "App": [
    "应用名",
    "應用程式名稱",
    "應用程式名稱",
    "アプリ名"
  ],
  "Stream": [
    "流名称",
    "串流名稱",
    "串流名稱",
    "ストリーム名"
  ],
  "Publish browser camera through WHIP": [
    "通过 WHIP 推送浏览器摄像头画面",
    "透過 WHIP 發佈瀏覽器攝影機畫面",
    "透過 WHIP 發布瀏覽器攝影機畫面",
    "ブラウザーのカメラをWHIPで配信"
  ],
  "Stop browser publish": [
    "停止浏览器推流",
    "停止瀏覽器串流",
    "停止瀏覽器串流",
    "ブラウザーからの配信を停止"
  ],
  "Connecting to WHEP...": [
    "正在连接 WHEP…",
    "正在連線至 WHEP…",
    "正在連線至 WHEP…",
    "WHEPに接続しています…"
  ],
  "Connected.": [
    "已连接。",
    "已連線。",
    "已連線。",
    "接続しました。"
  ],
  "Requesting browser camera...": [
    "正在请求摄像头权限…",
    "正在請求攝影機權限…",
    "正在要求攝影機權限…",
    "カメラの許可を求めています…"
  ],
  "Browser camera is idle.": [
    "浏览器摄像头空闲。",
    "瀏覽器攝影機閒置。",
    "瀏覽器攝影機閒置。",
    "ブラウザーのカメラは待機中です。"
  ],
  "Publish from iPhoneMirror through RTMP, SRT, or WHIP, then verify the same stream through WHEP.": [
    "通过 iPhoneMirror 以 RTMP、SRT 或 WHIP 推流，再通过 WHEP 验证同一条流。",
    "透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發佈串流，再透過 WHEP 驗證同一串流。",
    "透過 iPhoneMirror 以 RTMP、SRT 或 WHIP 發布串流，再透過 WHEP 驗證同一串流。",
    "iPhoneMirrorからRTMP・SRT・WHIPで配信し、同じストリームをWHEPで確かめます。"
  ],
  "The page focuses on video delivery. iPhoneMirror may include source audio when available; browser camera publishing is an independent SRS check.": [
    "此页面用于验证视频传输。iPhoneMirror 可在音源可用时包含音频；浏览器摄像头推流是独立的 SRS 测试。",
    "此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。",
    "此頁面用於驗證視訊傳輸。iPhoneMirror 可在音訊來源可用時包含音訊；瀏覽器攝影機串流是獨立的 SRS 測試。",
    "このページは映像の配信の確認用です。iPhoneMirrorは、音声があれば一緒に送ることがあります。ブラウザーのカメラからの配信は、SRSを単独で確かめるためのものです。"
  ],
  "Media server publish and playback endpoints": [
    "媒体服务器推流与播放地址",
    "媒體伺服器串流與播放位址",
    "媒體伺服器串流與播放位址",
    "メディアサーバーの配信と再生のアドレス"
  ],
  "Streams: {0}": [
    "流数量：{0}",
    "串流數量：{0}",
    "串流數量：{0}",
    "ストリーム: {0}"
  ],
  "Clients: {0}": [
    "客户端：{0}",
    "用戶端：{0}",
    "用戶端：{0}",
    "クライアント: {0}"
  ],
  "Camera {0}": [
    "摄像头 {0}",
    "攝影機 {0}",
    "攝影機 {0}",
    "カメラ {0}"
  ],
  "Video inputs: {0}. Selected: {1}.": [
    "视频输入：{0}。已选择：{1}。",
    "視訊輸入：{0}。已選擇：{1}。",
    "視訊輸入：{0}。已選擇：{1}。",
    "ビデオ入力: {0}。選択中: {1}。"
  ],
  "Frame analysis failed: {0}": [
    "帧分析失败：{0}",
    "影格分析失敗：{0}",
    "影格分析失敗：{0}",
    "フレームの解析に失敗しました: {0}"
  ],
  "Request returned HTTP {0}": [
    "请求返回 HTTP {0}",
    "請求傳回 HTTP {0}",
    "要求傳回 HTTP {0}",
    "要求がHTTP {0}を返しました"
  ],
  "Playback state: {0}": [
    "播放状态：{0}",
    "播放狀態：{0}",
    "播放狀態：{0}",
    "再生の状態: {0}"
  ],
  "Publish state: {0}": [
    "推流状态：{0}",
    "串流狀態：{0}",
    "串流狀態：{0}",
    "配信の状態: {0}"
  ],
  "{0} at {1}.": [
    "{0}（地址：{1}）。",
    "{0}（位址：{1}）。",
    "{0}（位址：{1}）。",
    "{0}（アドレス: {1}）。"
  ],
  "MediaMTX ready": [
    "MediaMTX 已就绪",
    "MediaMTX 已就緒",
    "MediaMTX 已就緒",
    "MediaMTXの準備ができました"
  ],
  "SRS ready": [
    "SRS 已就绪",
    "SRS 已就緒",
    "SRS 已就緒",
    "SRSの準備ができました"
  ],
  "unknown": [
    "未知",
    "未知",
    "未知",
    "不明"
  ],
  "video": [
    "视频",
    "視訊",
    "視訊",
    "ビデオ"
  ],
  "failed": [
    "失败",
    "失敗",
    "失敗",
    "失敗"
  ],
  "disconnected": [
    "已断开",
    "已中斷",
    "已中斷連線",
    "切断"
  ],
  "closed": [
    "已关闭",
    "已關閉",
    "已關閉",
    "終了"
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
