let whepUrl = 'http://127.0.0.1:1985/rtc/v1/whep/?app=live&stream=iphone-mirror';

const state = {
  whepConnection: null,
  whepResource: null,
  cameraStream: null,
  cameraFrameCallback: null,
  cameraFallbackTimer: null,
  cameraFrameTimes: [],
  cameraLastFrameAt: null,
  cameraLongestGap: 0,
  cameraBlackFrames: 0,
  cameraLastStatsRender: 0,
  cameraSelectionTouched: false,
};

const elements = {
  cameraAnalysis: document.querySelector('#camera-analysis'),
  cameraBlackValue: document.querySelector('#camera-black-value'),
  cameraDeviceValue: document.querySelector('#camera-device-value'),
  cameraFpsValue: document.querySelector('#camera-fps-value'),
  cameraGapValue: document.querySelector('#camera-gap-value'),
  cameraMessage: document.querySelector('#camera-message'),
  cameraResolutionValue: document.querySelector('#camera-resolution-value'),
  cameraSelect: document.querySelector('#camera-select'),
  cameraVideo: document.querySelector('#camera-video'),
  playerLink: document.querySelector('#player-link'),
  refresh: document.querySelector('#refresh-status'),
  scanCameras: document.querySelector('#scan-cameras'),
  serverDot: document.querySelector('#server-state .status-dot'),
  serverLabel: document.querySelector('#server-label'),
  startCamera: document.querySelector('#start-camera'),
  startWhep: document.querySelector('#start-whep'),
  stopCamera: document.querySelector('#stop-camera'),
  stopWhep: document.querySelector('#stop-whep'),
  streamCount: document.querySelector('#stream-count'),
  streamList: document.querySelector('#stream-list'),
  rtmpUrl: document.querySelector('#rtmp-url'),
  srtUrl: document.querySelector('#srt-url'),
  whipUrl: document.querySelector('#whip-url'),
  whepUrl: document.querySelector('#whep-url'),
  whepMessage: document.querySelector('#whep-message'),
  whepVideo: document.querySelector('#whep-video'),
};

function setServerState(kind, text) {
  elements.serverDot.className = `status-dot ${kind}`;
  elements.serverLabel.textContent = text;
}

function escapeHtml(value) {
  const map = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' };
  return String(value).replace(/[&<>"']/g, character => map[character]);
}

function describeError(error) {
  const name = String(error?.name || error?.constructor?.name || 'Error').trim();
  const message = String(error?.message || '').trim();
  return message && message !== name ? `${name}: ${message}` : name;
}

function renderStreams(payload) {
  const streams = payload.streams ?? [];
  elements.streamCount.textContent = `${streams.length} stream${streams.length === 1 ? '' : 's'}`;
  if (!streams.length) {
    elements.streamList.innerHTML = '<p>No active stream. Start RTMP, SRT, or WHIP from iPhoneMirror.</p>';
    return;
  }
  elements.streamList.innerHTML = streams.map(stream => {
    const name = stream.name ?? 'unknown';
    const clients = stream.clients ?? 0;
    const video = stream.video ?? 'video';
    return `<div class="stream-row"><strong>${escapeHtml(name)}</strong><span>${escapeHtml(video)}</span><span>${escapeHtml(clients)} clients</span></div>`;
  }).join('');
}

async function refreshStatus() {
  try {
    const response = await fetch('/api/status', { cache: 'no-store' });
    const payload = await response.json();
    if (!payload.ready) {
      setServerState('failed', 'Media server unavailable');
      elements.streamCount.textContent = '0 streams';
      elements.streamList.innerHTML = `<p>${escapeHtml(payload.error ?? 'No response from media server')} at ${escapeHtml(payload.api)}.</p>`;
      return;
    }
    setServerState('ready', payload.label ?? 'Media server ready');
    if (payload.endpoints) {
      elements.rtmpUrl.textContent = payload.endpoints.rtmp;
      elements.srtUrl.textContent = payload.endpoints.srt;
      elements.whipUrl.textContent = payload.endpoints.whip;
      elements.whepUrl.textContent = payload.endpoints.whep;
      elements.playerLink.href = payload.endpoints.player;
      whepUrl = payload.endpoints.whep;
    }
    renderStreams(payload);
  } catch (error) {
    setServerState('failed', 'Dashboard request failed');
    elements.streamList.innerHTML = `<p>${escapeHtml(error.message)}</p>`;
  }
}

function waitForIceGathering(connection) {
  if (connection.iceGatheringState === 'complete') return Promise.resolve();
  return new Promise(resolve => {
    const timeout = window.setTimeout(resolve, 1800);
    connection.addEventListener('icegatheringstatechange', () => {
      if (connection.iceGatheringState === 'complete') {
        window.clearTimeout(timeout);
        resolve();
      }
    }, { once: false });
  });
}

async function startWhep() {
  await stopWhep();
  elements.startWhep.disabled = true;
  elements.whepMessage.textContent = 'Negotiating WHEP playback';
  try {
    const connection = new RTCPeerConnection();
    connection.addTransceiver('video', { direction: 'recvonly' });
    connection.addEventListener('track', event => {
      elements.whepVideo.srcObject = event.streams[0];
      elements.whepMessage.textContent = '';
    });
    connection.addEventListener('connectionstatechange', () => {
      if (connection.connectionState === 'failed')
        elements.whepMessage.textContent = 'WebRTC connection failed';
    });
    const offer = await connection.createOffer();
    await connection.setLocalDescription(offer);
    await waitForIceGathering(connection);
    const response = await fetch(whepUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: connection.localDescription.sdp,
    });
    if (!response.ok) throw new Error(`WHEP returned ${response.status}`);
    const answer = await response.text();
    await connection.setRemoteDescription({ type: 'answer', sdp: answer });
    state.whepConnection = connection;
    state.whepResource = response.headers.get('location');
    elements.stopWhep.disabled = false;
    if (!elements.whepVideo.srcObject)
      elements.whepMessage.textContent = 'Waiting for video frames';
  } catch (error) {
    await stopWhep(false);
    elements.whepMessage.textContent = error.message;
  } finally {
    elements.startWhep.disabled = false;
  }
}

async function stopWhep(resetMessage = true) {
  const resource = state.whepResource;
  const connection = state.whepConnection;
  state.whepResource = null;
  state.whepConnection = null;
  elements.stopWhep.disabled = true;
  if (connection) connection.close();
  elements.whepVideo.srcObject = null;
  if (resource) {
    try { await fetch(new URL(resource, whepUrl), { method: 'DELETE' }); } catch { }
  }
  if (resetMessage)
    elements.whepMessage.textContent = 'Waiting for a live stream';
}

function populateCameras(cameras) {
  const previous = elements.cameraSelect.value;
  elements.cameraSelect.replaceChildren();
  if (!cameras.length) {
    elements.cameraSelect.add(new Option('No video input found', ''));
    return;
  }
  cameras.forEach((camera, index) => {
    elements.cameraSelect.add(new Option(
      camera.label || `Camera ${index + 1}`, camera.deviceId));
  });
  const previousStillExists = cameras.some(
    camera => camera.deviceId === previous);
  const virtualCamera = cameras.find(camera =>
    camera.label.toLowerCase().includes('iphonemirror virtual camera'));
  const selectedId = state.cameraSelectionTouched && previousStillExists
    ? previous
    : virtualCamera?.deviceId ||
      (previousStillExists ? previous : cameras[0].deviceId);
  elements.cameraSelect.value = selectedId;
}

async function enumerateCameras() {
  if (!navigator.mediaDevices?.enumerateDevices)
    throw new Error('Camera APIs are unavailable in this browser context.');
  const devices = await navigator.mediaDevices.enumerateDevices();
  const cameras = devices.filter(device => device.kind === 'videoinput');
  populateCameras(cameras);
  return cameras;
}

async function requestCameraStream(constraints) {
  const timeoutMs = 8000;
  let timedOut = false;
  let timeoutId;
  const request = navigator.mediaDevices.getUserMedia(constraints).then(stream => {
    if (timedOut) {
      stream.getTracks().forEach(track => track.stop());
      throw new Error('Camera permission request timed out.');
    }
    return stream;
  });
  const timeout = new Promise((resolve, reject) => {
    timeoutId = window.setTimeout(() => {
      timedOut = true;
      reject(new Error(
        'Camera permission request timed out. Allow camera access and scan again.'));
    }, timeoutMs);
  });
  try {
    return await Promise.race([request, timeout]);
  } finally {
    window.clearTimeout(timeoutId);
  }
}

async function scanCameras(requestPermission = true) {
  elements.scanCameras.disabled = true;
  try {
    if (requestPermission && !state.cameraStream) {
      const permissionStream = await requestCameraStream({
        video: true,
        audio: false,
      });
      permissionStream.getTracks().forEach(track => track.stop());
    }
    const cameras = await enumerateCameras();
    const selected = elements.cameraSelect.selectedOptions[0]?.textContent || '';
    elements.cameraMessage.textContent = cameras.length
      ? `${cameras.length} video input${cameras.length === 1 ? '' : 's'} found. Selected: ${selected}.`
      : 'No video input was found.';
    return cameras;
  } catch (error) {
    elements.cameraMessage.textContent = describeError(error);
    return [];
  } finally {
    elements.scanCameras.disabled = false;
  }
}

function resetCameraDiagnostics() {
  state.cameraFrameTimes = [];
  state.cameraLastFrameAt = null;
  state.cameraLongestGap = 0;
  state.cameraBlackFrames = 0;
  state.cameraLastStatsRender = 0;
  elements.cameraDeviceValue.textContent = 'Not open';
  elements.cameraResolutionValue.textContent = '--';
  elements.cameraFpsValue.textContent = '--';
  elements.cameraGapValue.textContent = '--';
  elements.cameraBlackValue.textContent = '0';
  elements.cameraBlackValue.classList.remove('alert');
}

function sampleIsNearBlack() {
  const video = elements.cameraVideo;
  if (video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA ||
      video.videoWidth === 0 || video.videoHeight === 0)
    return false;
  const context = elements.cameraAnalysis.getContext('2d', {
    alpha: false,
    willReadFrequently: true,
  });
  context.drawImage(video, 0, 0, elements.cameraAnalysis.width,
    elements.cameraAnalysis.height);
  const pixels = context.getImageData(
    0, 0, elements.cameraAnalysis.width, elements.cameraAnalysis.height).data;
  let total = 0;
  let maximum = 0;
  for (let index = 0; index < pixels.length; index += 4) {
    const luminance = (pixels[index] * 54 + pixels[index + 1] * 183 +
      pixels[index + 2] * 19) >> 8;
    total += luminance;
    maximum = Math.max(maximum, luminance);
  }
  const average = total / (pixels.length / 4);
  return average < 4 && maximum < 12;
}

function renderCameraDiagnostics(timestamp, force = false) {
  if (!state.cameraStream || (!force &&
      timestamp - state.cameraLastStatsRender < 200))
    return;
  state.cameraLastStatsRender = timestamp;
  const track = state.cameraStream.getVideoTracks()[0];
  const settings = track?.getSettings() || {};
  const width = elements.cameraVideo.videoWidth || settings.width;
  const height = elements.cameraVideo.videoHeight || settings.height;
  const times = state.cameraFrameTimes;
  const fps = times.length > 1
    ? (times.length - 1) * 1000 / (times[times.length - 1] - times[0])
    : 0;
  elements.cameraDeviceValue.textContent = track?.label ||
    elements.cameraSelect.selectedOptions[0]?.textContent || 'Video input';
  elements.cameraResolutionValue.textContent = width && height
    ? `${width} x ${height}` : '--';
  elements.cameraFpsValue.textContent = fps > 0 ? fps.toFixed(1) : '--';
  elements.cameraGapValue.textContent = state.cameraLongestGap > 0
    ? `${state.cameraLongestGap.toFixed(1)} ms` : '--';
  elements.cameraBlackValue.textContent = String(state.cameraBlackFrames);
  elements.cameraBlackValue.classList.toggle(
    'alert', state.cameraBlackFrames > 0);
}

function handleCameraFrame(timestamp) {
  if (!state.cameraStream) return;
  if (state.cameraLastFrameAt !== null) {
    state.cameraLongestGap = Math.max(
      state.cameraLongestGap, timestamp - state.cameraLastFrameAt);
  }
  state.cameraLastFrameAt = timestamp;
  state.cameraFrameTimes.push(timestamp);
  while (state.cameraFrameTimes.length > 1 &&
         state.cameraFrameTimes[0] < timestamp - 2000)
    state.cameraFrameTimes.shift();
  try {
    if (sampleIsNearBlack()) ++state.cameraBlackFrames;
  } catch (error) {
    elements.cameraMessage.textContent =
      `Frame analysis failed: ${describeError(error)}`;
  }
  renderCameraDiagnostics(timestamp);
  state.cameraFrameCallback = elements.cameraVideo.requestVideoFrameCallback(
    handleCameraFrame);
}

function startCameraDiagnostics() {
  resetCameraDiagnostics();
  if (typeof elements.cameraVideo.requestVideoFrameCallback === 'function') {
    state.cameraFrameCallback = elements.cameraVideo.requestVideoFrameCallback(
      handleCameraFrame);
    return;
  }
  let lastDecodedFrames = 0;
  state.cameraFallbackTimer = window.setInterval(() => {
    const timestamp = performance.now();
    const decodedFrames = elements.cameraVideo.getVideoPlaybackQuality?.()
      .totalVideoFrames || 0;
    if (decodedFrames > lastDecodedFrames) {
      lastDecodedFrames = decodedFrames;
      if (state.cameraLastFrameAt !== null) {
        state.cameraLongestGap = Math.max(
          state.cameraLongestGap, timestamp - state.cameraLastFrameAt);
      }
      state.cameraLastFrameAt = timestamp;
      state.cameraFrameTimes.push(timestamp);
      try {
        if (sampleIsNearBlack()) ++state.cameraBlackFrames;
      } catch { }
    }
    renderCameraDiagnostics(timestamp, true);
  }, 250);
}

function stopCameraDiagnostics() {
  if (state.cameraFrameCallback !== null &&
      typeof elements.cameraVideo.cancelVideoFrameCallback === 'function') {
    elements.cameraVideo.cancelVideoFrameCallback(state.cameraFrameCallback);
  }
  state.cameraFrameCallback = null;
  if (state.cameraFallbackTimer !== null)
    window.clearInterval(state.cameraFallbackTimer);
  state.cameraFallbackTimer = null;
}

async function startCamera() {
  await stopCamera(false);
  elements.startCamera.disabled = true;
  try {
    await scanCameras(true);
    const deviceId = elements.cameraSelect.value;
    if (!deviceId) throw new Error('No video input is available.');
    const stream = await requestCameraStream({
      video: { deviceId: { exact: deviceId } },
      audio: false,
    });
    state.cameraStream = stream;
    elements.cameraVideo.srcObject = stream;
    await elements.cameraVideo.play();
    elements.stopCamera.disabled = false;
    startCameraDiagnostics();
    await enumerateCameras();
    renderCameraDiagnostics(performance.now(), true);
    elements.cameraMessage.textContent = 'Camera is delivering frames.';
  } catch (error) {
    await stopCamera(false);
    elements.cameraMessage.textContent = describeError(error);
  } finally {
    elements.startCamera.disabled = false;
  }
}

async function stopCamera(updateMessage = true) {
  stopCameraDiagnostics();
  state.cameraStream?.getTracks().forEach(track => track.stop());
  state.cameraStream = null;
  elements.cameraVideo.srcObject = null;
  elements.stopCamera.disabled = true;
  resetCameraDiagnostics();
  if (updateMessage) elements.cameraMessage.textContent = 'Camera is not open.';
}

elements.refresh.addEventListener('click', refreshStatus);
elements.scanCameras.addEventListener('click', () => scanCameras(true));
elements.startWhep.addEventListener('click', startWhep);
elements.stopWhep.addEventListener('click', stopWhep);
elements.startCamera.addEventListener('click', startCamera);
elements.stopCamera.addEventListener('click', stopCamera);
elements.cameraSelect.addEventListener('change', () => {
  state.cameraSelectionTouched = true;
});
elements.whepVideo.addEventListener('playing', () => {
  elements.whepMessage.textContent = '';
});
window.addEventListener('beforeunload', () => { stopWhep(); stopCamera(); });

refreshStatus();
scanCameras(false);
if (navigator.mediaDevices)
  navigator.mediaDevices.addEventListener('devicechange', () => scanCameras(false));
window.setInterval(refreshStatus, 3000);
