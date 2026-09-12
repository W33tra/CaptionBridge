const output = document.querySelector("#console");
const finalView = document.body.dataset.view === "final";
const wrongBrowser = document.querySelector("#wrong-browser");
const tests = document.querySelector("#tests");
const audioPlayer = document.querySelector("#audio-player");
const fileInput = document.querySelector("#file-input");
const chromeError = document.querySelector("#chrome-error");
const sessionSelect = document.querySelector("#session-select");
const browserMode = document.querySelector("#browser-mode");
const overlaySettings = document.querySelector("#overlay-settings");
const overlayStatus = document.querySelector("#overlay-status");
const overlayFontFamily = document.querySelector("#overlay-font-family");
const overlayClickThrough = document.querySelector("#overlay-click-through");
const nativeCaptionPlacement = document.querySelector("#native-caption-placement");
const overlayProfileName = document.querySelector("#overlay-profile-name");
const overlayProfileSelect = document.querySelector("#overlay-profile-select");
const customFontFile = document.querySelector("#custom-font-file");
const overlayGeometry = document.querySelector("#overlay-geometry");
const overlayControls = [
  [document.querySelector("#overlay-width"), document.querySelector("#overlay-width-value")],
  [document.querySelector("#overlay-height"), document.querySelector("#overlay-height-value")],
  [document.querySelector("#overlay-font-size"), document.querySelector("#overlay-font-size-value")],
  [document.querySelector("#overlay-opacity"), document.querySelector("#overlay-opacity-value")],
];
const overlayStorageKey = "captionBridge.overlaySettings";
const overlayProfilesStorageKey = "captionBridge.overlayProfiles";
const videoPlayer = document.querySelector("#video-player");
const streamCanvas = document.querySelector("#stream-canvas");

let socket = null;
let controlSocket = null;
let audioContext = null;
let scheduledNodes = [];
let nextStartTime = 0;
let streamFormat = null;
let localObjectUrl = null;
let pageClosing = false;
let detectedBrowser = "other";
let audioOutputNode = null;
let mediaDestination = null;
let canvasStream = null;
let canvasTimer = null;
let overlayActive = false;
let liveOverlayUpdateRunning = false;
let liveOverlayUpdateQueued = false;
let overlayStateSyncRunning = false;
let overlaySizeControlActive = false;

function isYandexBrowser() {
  return /YaBrowser\//.test(navigator.userAgent);
}

function isGoogleChrome() {
  if (isYandexBrowser()) return false;
  const brands = navigator.userAgentData?.brands;
  if (brands) return brands.some((brand) => brand.brand === "Google Chrome");
  const ua = navigator.userAgent;
  return /Chrome\//.test(ua) && !/(Edg|OPR|Opera|Vivaldi|Brave)\//.test(ua);
}

async function prepareAudioOutput() {
  if (detectedBrowser !== "yandex") {
    audioOutputNode = audioContext.destination;
    videoPlayer.hidden = true;
    return;
  }

  if (!mediaDestination) {
    mediaDestination = audioContext.createMediaStreamDestination();
    const context = streamCanvas.getContext("2d");
    const drawFrame = () => {
      context.fillStyle = "#050705";
      context.fillRect(0, 0, streamCanvas.width, streamCanvas.height);
      context.fillStyle = "#b7f7c2";
      context.font = "28px Consolas, monospace";
      context.textAlign = "center";
      context.fillText("CaptionBridge", streamCanvas.width / 2, 155);
      context.font = "18px Consolas, monospace";
      context.fillText("Прямой аудиопоток Windows", streamCanvas.width / 2, 200);
      context.fillText(new Date().toLocaleTimeString("ru-RU"), streamCanvas.width / 2, 240);
    };
    drawFrame();
    canvasTimer = setInterval(drawFrame, 250);
    canvasStream = streamCanvas.captureStream(4);
    const mediaStream = new MediaStream([
      ...canvasStream.getVideoTracks(),
      ...mediaDestination.stream.getAudioTracks(),
    ]);
    videoPlayer.srcObject = mediaStream;
  }
  audioOutputNode = mediaDestination;
  videoPlayer.hidden = false;
  await videoPlayer.play();
}

function write(message) {
  output.textContent += `\n${message}`;
}

function reportEvent(event) {
  fetch("/api/event", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ event }),
  }).catch(() => {});
}

function connectControlSocket() {
  if (pageClosing || controlSocket?.readyState === WebSocket.OPEN) return;
  const protocol = location.protocol === "https:" ? "wss" : "ws";
  controlSocket = new WebSocket(`${protocol}://${location.host}/ws/control`);
  controlSocket.onclose = () => {
    controlSocket = null;
    if (!pageClosing) setTimeout(connectControlSocket, 500);
  };
}

async function stopStream(showMessage = true) {
  if (socket?.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ command: "stop" }));
  }
  socket?.close();
  socket = null;
  for (const node of scheduledNodes) {
    try { node.stop(); } catch (_) {}
  }
  scheduledNodes = [];
  nextStartTime = 0;
  streamFormat = null;
  if (showMessage) write("Поток остановлен.");
}

function schedulePcm(arrayBuffer) {
  if (!audioContext || !streamFormat) return;
  const samples = new Int16Array(arrayBuffer);
  const frameCount = Math.floor(samples.length / streamFormat.channels);
  const buffer = audioContext.createBuffer(
    streamFormat.channels,
    frameCount,
    streamFormat.sampleRate,
  );
  for (let channel = 0; channel < streamFormat.channels; channel += 1) {
    const destination = buffer.getChannelData(channel);
    for (let frame = 0; frame < frameCount; frame += 1) {
      destination[frame] = samples[frame * streamFormat.channels + channel] / 32768;
    }
  }

  const node = audioContext.createBufferSource();
  node.buffer = buffer;
  node.connect(audioOutputNode || audioContext.destination);
  scheduledNodes.push(node);
  node.onended = () => {
    scheduledNodes = scheduledNodes.filter((item) => item !== node);
  };
  // Build a small initial cushion, then preserve the continuous timeline. The
  // server sends 200 ms chunks in real time, so ordinary event-loop jitter
  // should not introduce a gap between scheduled AudioBufferSourceNodes.
  const earliest = audioContext.currentTime + (nextStartTime === 0 ? 0.3 : 0.05);
  nextStartTime = Math.max(nextStartTime, earliest);
  node.start(nextStartTime);
  nextStartTime += buffer.duration;
}

async function startSocket(command, message) {
  await stopStream(false);
  audioPlayer.pause();
  audioContext = audioContext || new AudioContext({ latencyHint: "playback" });
  await audioContext.resume();
  await prepareAudioOutput();

  const protocol = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${protocol}://${location.host}/ws/audio`);
  socket.binaryType = "arraybuffer";
  socket.onopen = () => {
    socket.send(JSON.stringify(command));
    write(message);
  };
  socket.onmessage = (event) => {
    if (typeof event.data === "string") {
      const message = JSON.parse(event.data);
      if (message.type === "format") {
        streamFormat = message;
        write(`PCM: ${message.sampleRate} Гц, каналов: ${message.channels}, 16 бит`);
      } else if (message.type === "end") {
        write("Сервер достиг конца PCM-потока.");
      } else if (message.type === "error") {
        write(`Ошибка захвата: ${message.message}`);
      }
    } else {
      schedulePcm(event.data);
    }
  };
  socket.onerror = () => write("Ошибка WebSocket. Проверьте консоль CaptionBridge.");
}

function startStream() {
  return startSocket(
    { command: "start" },
    "Передача тестовой речи через Web Audio...",
  );
}

function startSelectedProcess() {
  const pid = Number(sessionSelect.value);
  if (!pid) {
    write("Сначала выберите приложение с активным аудиопотоком.");
    return;
  }
  const label = sessionSelect.options[sessionSelect.selectedIndex].textContent;
  return startSocket(
    { command: "capture-process", pid },
    `Захват ${label} через Windows Process Loopback...`,
  );
}

async function refreshSessions() {
  sessionSelect.disabled = true;
  sessionSelect.replaceChildren(new Option("Чтение аудиосессий Windows...", ""));
  try {
    const response = await fetch("/api/sessions");
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "Не удалось прочитать аудиосессии.");
    sessionSelect.replaceChildren();
    if (result.sessions.length === 0) {
      sessionSelect.add(new Option("Активные аудиоприложения не найдены", ""));
      write("Активных аудиосессий нет. Включите звук в приложении и обновите список.");
    } else {
      for (const session of result.sessions) {
        const title = session.WindowTitle ? ` — ${session.WindowTitle}` : "";
        sessionSelect.add(new Option(`${session.Name}${title} (PID ${session.Pid})`, session.Pid));
      }
      write(`Найдено активных аудиоприложений: ${result.sessions.length}.`);
    }
  } catch (error) {
    sessionSelect.replaceChildren(new Option("Не удалось получить список", ""));
    write(`Ошибка списка приложений: ${error.message}`);
  } finally {
    sessionSelect.disabled = false;
  }
}

async function setOverlay(enabled, resetPosition = false, quiet = false, position = null) {
  const width = Number(document.querySelector("#overlay-width").value);
  const height = Number(document.querySelector("#overlay-height").value);
  const fontSize = Number(document.querySelector("#overlay-font-size").value);
  const fontFamily = overlayFontFamily.value;
  const opacity = Number(document.querySelector("#overlay-opacity").value);
  const clickThrough = overlayClickThrough.checked;
  const nativePlacement = nativeCaptionPlacement.value;
  if (!quiet) {
    overlayStatus.textContent = enabled
      ? "Запуск отдельного окна оверлея..."
      : "Закрытие оверлея...";
  }
  try {
    const response = await fetch("/api/overlay-settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        enabled,
        width,
        height,
        fontSize,
        fontFamily,
        opacity,
        resetPosition,
        quiet,
        clickThrough,
        nativeCaptionPlacement: nativePlacement,
        x: position?.x ?? null,
        y: position?.y ?? null,
      }),
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "Не удалось применить настройку.");
    if (!enabled) {
      overlayActive = false;
      overlayStatus.textContent = "Оверлей закрыт.";
      return true;
    }
    overlayActive = true;
    overlayStatus.textContent = quiet
      ? `Сейчас: ${width}×${height}, ${overlayFontFamily.options[overlayFontFamily.selectedIndex].textContent}, ${fontSize}px, непрозрачность ${opacity}%, ${clickThrough ? "щелчки проходят сквозь окно" : "окно можно перетаскивать"}.`
      : result.created
        ? `Отдельное окно открыто наверху экрана: ${width}×${height}, шрифт ${fontSize}, непрозрачность ${opacity}%.`
        : `Настройки применены без перезапуска: ${width}×${height}, шрифт ${fontSize}, непрозрачность ${opacity}%.`;
    if (!quiet) {
      write(result.created
        ? "Открыто отдельное изменяемое окно CaptionBridge — субтитры."
        : "Настройки открытого оверлея обновлены.");
    }
    return true;
  } catch (error) {
    overlayStatus.textContent = `Ошибка: ${error.message}`;
    return false;
  }
}

function syncOverlayControlValues() {
  for (const [input, outputValue] of overlayControls) {
    outputValue.value = input.value;
  }
}

function saveOverlayControlValues() {
  const values = Object.fromEntries(
    overlayControls.map(([input]) => [input.id, Number(input.value)]),
  );
  values[overlayFontFamily.id] = overlayFontFamily.value;
  values[overlayClickThrough.id] = overlayClickThrough.checked;
  values[nativeCaptionPlacement.id] = nativeCaptionPlacement.value;
  localStorage.setItem(overlayStorageKey, JSON.stringify(values));
}

function loadOverlayControlValues() {
  try {
    const values = JSON.parse(localStorage.getItem(overlayStorageKey) || "{}");
    for (const [input] of overlayControls) {
      const value = Number(values[input.id]);
      if (Number.isFinite(value) && value >= Number(input.min) && value <= Number(input.max)) {
        input.value = String(value);
      }
    }
    if ([...overlayFontFamily.options].some((option) => option.value === values[overlayFontFamily.id])) {
      overlayFontFamily.value = values[overlayFontFamily.id];
    }
    overlayClickThrough.checked = values[overlayClickThrough.id] === true;
    if ([...nativeCaptionPlacement.options].some((option) => option.value === values[nativeCaptionPlacement.id])) {
      nativeCaptionPlacement.value = values[nativeCaptionPlacement.id];
    }
  } catch (_) {
    localStorage.removeItem(overlayStorageKey);
  }
}

async function refreshCustomFonts(preferredFont = "") {
  try {
    const response = await fetch("/api/fonts");
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "Не удалось получить список шрифтов.");

    const previous = preferredFont || overlayFontFamily.value;
    for (const option of [...overlayFontFamily.options]) {
      if (option.dataset.custom === "true") option.remove();
    }
    for (const font of result.fonts || []) {
      const option = new Option(`${font.label} — пользовательский`, font.id);
      option.dataset.custom = "true";
      overlayFontFamily.add(option);
    }
    if ([...overlayFontFamily.options].some((option) => option.value === previous)) {
      overlayFontFamily.value = previous;
    }
  } catch (error) {
    overlayStatus.textContent = `Ошибка списка пользовательских шрифтов: ${error.message}`;
  }
}

async function addCustomFont() {
  const [file] = customFontFile.files;
  if (!file) {
    overlayStatus.textContent = "Сначала выберите файл шрифта .ttf или .otf.";
    return;
  }

  const form = new FormData();
  form.append("font", file, file.name);
  overlayStatus.textContent = `Добавление шрифта ${file.name}...`;
  try {
    const response = await fetch("/api/fonts", { method: "POST", body: form });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "Не удалось добавить шрифт.");
    await refreshCustomFonts(result.font.id);
    overlayFontFamily.value = result.font.id;
    saveOverlayControlValues();
    overlayStatus.textContent = `Шрифт «${result.font.label}» добавлен и выбран.`;
    if (overlayActive) queueLiveOverlayUpdate();
    customFontFile.value = "";
  } catch (error) {
    overlayStatus.textContent = `Ошибка добавления шрифта: ${error.message}`;
  }
}

function readOverlayProfiles() {
  try {
    const profiles = JSON.parse(localStorage.getItem(overlayProfilesStorageKey) || "{}");
    return profiles && typeof profiles === "object" && !Array.isArray(profiles) ? profiles : {};
  } catch (_) {
    localStorage.removeItem(overlayProfilesStorageKey);
    return {};
  }
}

function refreshOverlayProfileSelect(selectedName = "") {
  const profiles = readOverlayProfiles();
  const names = Object.keys(profiles).sort((left, right) => left.localeCompare(right, "ru"));
  overlayProfileSelect.replaceChildren();
  if (names.length === 0) {
    overlayProfileSelect.add(new Option("Профили пока не сохранены", ""));
    return;
  }
  overlayProfileSelect.add(new Option("Выберите профиль", ""));
  for (const name of names) overlayProfileSelect.add(new Option(name, name));
  if (selectedName && profiles[selectedName]) overlayProfileSelect.value = selectedName;
}

function applyProfileValue(input, value) {
  const numericValue = Number(value);
  if (Number.isFinite(numericValue)
      && numericValue >= Number(input.min)
      && numericValue <= Number(input.max)) {
    input.value = String(numericValue);
  }
}

async function saveOverlayProfile() {
  const name = overlayProfileName.value.trim();
  if (!name) {
    overlayStatus.textContent = "Введите название профиля, например «Дота».";
    overlayProfileName.focus();
    return;
  }

  try {
    const response = await fetch("/api/overlay-state");
    const state = await response.json();
    if (!response.ok) throw new Error(state.error || "Не удалось прочитать положение окна.");
    if (!state.enabled || !Number.isFinite(state.x) || !Number.isFinite(state.y)) {
      throw new Error("Сначала откройте и расположите оверлей, затем сохраните профиль.");
    }

    const profiles = readOverlayProfiles();
    profiles[name] = {
      x: state.x,
      y: state.y,
      width: state.width,
      height: state.height,
      fontSize: Number(document.querySelector("#overlay-font-size").value),
      fontFamily: overlayFontFamily.value,
      opacity: Number(document.querySelector("#overlay-opacity").value),
      clickThrough: overlayClickThrough.checked,
      nativeCaptionPlacement: nativeCaptionPlacement.value,
    };
    localStorage.setItem(overlayProfilesStorageKey, JSON.stringify(profiles));
    applyProfileValue(document.querySelector("#overlay-width"), state.width);
    applyProfileValue(document.querySelector("#overlay-height"), state.height);
    syncOverlayControlValues();
    saveOverlayControlValues();
    refreshOverlayProfileSelect(name);
    overlayStatus.textContent = `Профиль «${name}» сохранён: координаты ${state.x}, ${state.y}; размер ${state.width}×${state.height}.`;
  } catch (error) {
    overlayStatus.textContent = `Ошибка сохранения профиля: ${error.message}`;
  }
}

async function loadOverlayProfile() {
  const name = overlayProfileSelect.value;
  const profile = readOverlayProfiles()[name];
  if (!profile) {
    overlayStatus.textContent = "Выберите сохранённый профиль.";
    return;
  }

  applyProfileValue(document.querySelector("#overlay-width"), profile.width);
  applyProfileValue(document.querySelector("#overlay-height"), profile.height);
  applyProfileValue(document.querySelector("#overlay-font-size"), profile.fontSize);
  applyProfileValue(document.querySelector("#overlay-opacity"), profile.opacity);
  if ([...overlayFontFamily.options].some((option) => option.value === profile.fontFamily)) {
    overlayFontFamily.value = profile.fontFamily;
  }
  overlayClickThrough.checked = profile.clickThrough === true;
  if ([...nativeCaptionPlacement.options].some((option) => option.value === profile.nativeCaptionPlacement)) {
    nativeCaptionPlacement.value = profile.nativeCaptionPlacement;
  }
  overlayProfileName.value = name;
  syncOverlayControlValues();
  saveOverlayControlValues();
  const loaded = await setOverlay(true, false, false, { x: profile.x, y: profile.y });
  if (loaded) {
    overlayStatus.textContent = `Профиль «${name}» загружен. Оверлей перемещён в сохранённое место.`;
  }
}

function deleteOverlayProfile() {
  const name = overlayProfileSelect.value;
  const profiles = readOverlayProfiles();
  if (!name || !profiles[name]) {
    overlayStatus.textContent = "Выберите профиль, который нужно удалить.";
    return;
  }
  delete profiles[name];
  localStorage.setItem(overlayProfilesStorageKey, JSON.stringify(profiles));
  overlayProfileName.value = "";
  refreshOverlayProfileSelect();
  overlayStatus.textContent = `Профиль «${name}» удалён.`;
}

function queueLiveOverlayUpdate() {
  syncOverlayControlValues();
  saveOverlayControlValues();
  if (!overlayActive) {
    overlayStatus.textContent = "Значения выбраны. Откройте оверлей, чтобы увидеть их.";
    return;
  }

  liveOverlayUpdateQueued = true;
  if (liveOverlayUpdateRunning) return;
  liveOverlayUpdateRunning = true;
  void (async () => {
    while (liveOverlayUpdateQueued && overlayActive) {
      liveOverlayUpdateQueued = false;
      await setOverlay(true, false, true);
      await new Promise((resolve) => setTimeout(resolve, 60));
    }
    liveOverlayUpdateRunning = false;
  })();
}

async function syncOverlayWindowState() {
  if (overlayStateSyncRunning)
    return;
  overlayStateSyncRunning = true;
  try {
    const response = await fetch("/api/overlay-state");
    const state = await response.json();
    if (!response.ok) return;
    overlayActive = state.enabled === true;
    if (!overlayActive || !Number.isFinite(state.x) || !Number.isFinite(state.y)) {
      overlayGeometry.textContent = "Фактические координаты и размер появятся после открытия оверлея.";
      return;
    }

    overlayGeometry.textContent = `Положение: X ${state.x}, Y ${state.y}; фактический размер ${state.width}×${state.height}.`;
    if (overlaySizeControlActive || liveOverlayUpdateRunning)
      return;

    const widthInput = document.querySelector("#overlay-width");
    const heightInput = document.querySelector("#overlay-height");
    applyProfileValue(widthInput, state.width);
    applyProfileValue(heightInput, state.height);
    syncOverlayControlValues();
    saveOverlayControlValues();
  } catch (_) {
  } finally {
    overlayStateSyncRunning = false;
  }
}

function playAudio(source) {
  stopStream(false);
  audioPlayer.src = source;
  audioPlayer.hidden = false;
  audioPlayer.play().catch((error) => write(`Ошибка воспроизведения: ${error.message}`));
}

document.querySelector("#stream-test").addEventListener("click", startStream);
document.querySelector("#restart-test").addEventListener("click", startStream);
document.querySelector("#refresh-apps").addEventListener("click", refreshSessions);
document.querySelector("#process-test").addEventListener("click", startSelectedProcess);
document.querySelector("#stop-test").addEventListener("click", () => stopStream());
document.querySelector("#start-overlay").addEventListener("click", () => setOverlay(true, true));
document.querySelector("#apply-overlay").addEventListener("click", () => setOverlay(true));
document.querySelector("#return-overlay").addEventListener("click", () => setOverlay(true, true));
document.querySelector("#stop-overlay").addEventListener("click", () => setOverlay(false));
for (const [input] of overlayControls) {
  input.addEventListener("input", queueLiveOverlayUpdate);
  if (input.id === "overlay-width" || input.id === "overlay-height") {
    input.addEventListener("pointerdown", () => { overlaySizeControlActive = true; });
  }
}
window.addEventListener("pointerup", () => { overlaySizeControlActive = false; });
window.addEventListener("pointercancel", () => { overlaySizeControlActive = false; });
overlayFontFamily.addEventListener("change", queueLiveOverlayUpdate);
overlayClickThrough.addEventListener("change", queueLiveOverlayUpdate);
nativeCaptionPlacement.addEventListener("change", queueLiveOverlayUpdate);
document.querySelector("#add-custom-font").addEventListener("click", addCustomFont);
document.querySelector("#save-overlay-profile").addEventListener("click", saveOverlayProfile);
document.querySelector("#load-overlay-profile").addEventListener("click", loadOverlayProfile);
document.querySelector("#delete-overlay-profile").addEventListener("click", deleteOverlayProfile);
overlayProfileSelect.addEventListener("change", () => {
  if (overlayProfileSelect.value) overlayProfileName.value = overlayProfileSelect.value;
});
loadOverlayControlValues();
syncOverlayControlValues();
refreshOverlayProfileSelect();
window.setInterval(syncOverlayWindowState, 350);
document.querySelector("#open-yandex").addEventListener("click", async () => {
  try {
    const response = await fetch("/api/open-yandex", { method: "POST" });
    const result = await response.json();
    if (!response.ok) throw new Error(`${result.error}\n${result.checkedPaths.join("\n")}`);
    write("Страница эксперимента открыта в Яндекс Браузере.");
  } catch (error) {
    write(`Не удалось открыть Яндекс Браузер: ${error.message}`);
  }
});
document.querySelector("#file-test").addEventListener("click", () => {
  write("Воспроизведение русской тестовой речи через <audio>...");
  playAudio(`/audio/test_audio_ru.wav?cache=${Date.now()}`);
});
document.querySelector("#local-test").addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", () => {
  const [file] = fileInput.files;
  if (!file) return;
  if (localObjectUrl) URL.revokeObjectURL(localObjectUrl);
  localObjectUrl = URL.createObjectURL(file);
  write(`Воспроизведение локального файла: ${file.name}`);
  playAudio(localObjectUrl);
});
audioPlayer.addEventListener("play", () => reportEvent("audio-started"));
audioPlayer.addEventListener("ended", () => reportEvent("audio-stopped"));
audioPlayer.addEventListener("pause", () => {
  if (!audioPlayer.ended) reportEvent("audio-stopped");
});

document.querySelector("#open-chrome").addEventListener("click", async () => {
  chromeError.hidden = true;
  const response = await fetch("/api/open-chrome", { method: "POST" });
  const result = await response.json();
  if (response.ok) {
    output.textContent = "Страница открыта в Google Chrome.\nЭту вкладку можно закрыть.";
    wrongBrowser.hidden = true;
  } else {
    chromeError.textContent = `${result.error}\nПроверены пути:\n${result.checkedPaths.join("\n")}`;
    chromeError.hidden = false;
  }
});

document.addEventListener("keydown", (event) => {
  if (event.target.matches("input, button")) return;
  if (event.key === "1") startStream();
  if (event.key === "2") document.querySelector("#file-test").click();
  if (event.key === "3") fileInput.click();
  if (event.key.toLowerCase() === "r") startStream();
  if (event.key.toLowerCase() === "a") refreshSessions();
  if (event.key.toLowerCase() === "p") startSelectedProcess();
  if (event.key.toLowerCase() === "s") stopStream();
  if (event.key.toLowerCase() === "о") refreshSessions();
  if (event.key.toLowerCase() === "з") startSelectedProcess();
  if (event.key.toLowerCase() === "с") stopStream();
  if (event.key.toLowerCase() === "к") startStream();
});

window.addEventListener("beforeunload", () => {
  pageClosing = true;
  controlSocket?.close();
  socket?.close();
  if (canvasTimer) clearInterval(canvasTimer);
  canvasStream?.getTracks().forEach((track) => track.stop());
  if (localObjectUrl) URL.revokeObjectURL(localObjectUrl);
});

async function initialize() {
  await refreshCustomFonts();
  loadOverlayControlValues();
  syncOverlayControlValues();
  const response = await fetch("/api/status");
  const status = await response.json();
  try {
    const overlayResponse = await fetch("/api/overlay-state");
    const currentOverlay = await overlayResponse.json();
    overlayActive = overlayResponse.ok && currentOverlay.enabled === true;
    if (overlayActive) {
      overlayStatus.textContent = Number.isFinite(currentOverlay.x)
        ? `Оверлей уже открыт: координаты ${currentOverlay.x}, ${currentOverlay.y}; размер ${currentOverlay.width}×${currentOverlay.height}.`
        : "Оверлей уже открыт.";
    }
  } catch (_) {
    overlayActive = false;
  }
  if (!finalView && isYandexBrowser()) {
    detectedBrowser = "yandex";
    document.querySelector("#open-yandex").hidden = true;
    output.textContent = "Яндекс Браузер: экспериментальный режим\n\nПосле запуска потока наведите указатель на видеоплеер и включите субтитры средствами браузера.";
    browserMode.textContent = "Режим вывода: живой HTML5-видеопоток.\nЕсли появится кнопка выносного видео, его окно можно разместить поверх игры и изменить его размер.";
    overlaySettings.hidden = true;
    tests.hidden = false;
    connectControlSocket();
    await refreshSessions();
    return;
  }
  if (isGoogleChrome()) {
    detectedBrowser = "chrome";
    output.textContent = finalView
      ? `Google Chrome готов.\n${status.chromePath || "Путь к chrome.exe недоступен"}`
      : `Chrome: OK\n${status.chromePath || "Путь к chrome.exe недоступен"}\n\nПеред проверкой:\nChrome → Настройки → Специальные возможности\n→ Автоматические субтитры = ВКЛ.`;
    tests.hidden = false;
    browserMode.textContent = finalView
      ? "Выберите приложение с активным звуком или сначала проиграйте пример русской речи."
      : "Режим вывода: Web Audio для Chrome Live Caption.";
    connectControlSocket();
    await refreshSessions();
  } else {
    output.textContent = finalView
      ? "Откройте CaptionBridge в Google Chrome."
      : "Текущий браузер — не Google Chrome";
    wrongBrowser.hidden = false;
    if (!status.chromeFound) {
      chromeError.textContent = `Chrome не найден. Проверены пути:\n${status.checkedPaths.join("\n")}`;
      chromeError.hidden = false;
    }
  }
}

initialize().catch((error) => {
  output.textContent = `Ошибка запуска CaptionBridge: ${error.message}`;
});
