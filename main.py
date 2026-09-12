from __future__ import annotations

import asyncio
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import wave

from aiohttp import WSMsgType, web


HOST = "127.0.0.1"
PORT = 17321
ROOT = Path(__file__).resolve().parent
WEB_DIR = ROOT / "web"
AUDIO_DIR = ROOT / "audio"
TEST_AUDIO = AUDIO_DIR / "test_audio_ru.wav"
CAPTURE_PROJECT = ROOT / "capture_helper" / "CaptionBridge.Capture.csproj"
CAPTURE_EXE = ROOT / "capture_helper" / "bin" / "Release" / "net8.0-windows" / "CaptionBridge.Host.exe"
OVERLAY_PROJECT = ROOT / "overlay_helper" / "CaptionBridge.Overlay.csproj"
OVERLAY_EXE = ROOT / "overlay_helper" / "bin" / "Release" / "net8.0-windows" / "CaptionBridge.Overlay.exe"
OVERLAY_SETTINGS_FILE = Path(os.environ.get("LOCALAPPDATA", ROOT)) / "CaptionBridge" / "overlay-settings.json"
OVERLAY_WINDOW_STATE_FILE = Path(os.environ.get("LOCALAPPDATA", ROOT)) / "CaptionBridge" / "overlay-state.json"
CUSTOM_FONTS_DIR = Path(os.environ.get("LOCALAPPDATA", ROOT)) / "CaptionBridge" / "fonts"
URL = f"http://{HOST}:{PORT}"
_capture_build_lock: asyncio.Lock | None = None
_overlay_build_lock: asyncio.Lock | None = None
STOP_EVENT = web.AppKey("stop_event", asyncio.Event)
CONTROL_STATE = web.AppKey("control_state", dict)
OVERLAY_STATE = web.AppKey("overlay_state", dict)


def chrome_candidates() -> list[Path]:
    candidates: list[Path] = []
    for variable in ("PROGRAMFILES", "PROGRAMFILES(X86)", "LOCALAPPDATA"):
        base = os.environ.get(variable)
        if base:
            candidates.append(Path(base) / "Google" / "Chrome" / "Application" / "chrome.exe")

    if sys.platform == "win32":
        try:
            import winreg

            for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
                for access in (winreg.KEY_READ, winreg.KEY_READ | winreg.KEY_WOW64_32KEY, winreg.KEY_READ | winreg.KEY_WOW64_64KEY):
                    try:
                        with winreg.OpenKey(
                            hive,
                            r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                            0,
                            access,
                        ) as key:
                            candidates.append(Path(winreg.QueryValue(key, None)))
                    except OSError:
                        pass
        except ImportError:
            pass

    unique: list[Path] = []
    seen: set[str] = set()
    for candidate in candidates:
        normalized = str(candidate).casefold()
        if normalized not in seen:
            unique.append(candidate)
            seen.add(normalized)
    return unique


def find_chrome() -> tuple[Path | None, list[Path]]:
    candidates = chrome_candidates()
    return next((path for path in candidates if path.is_file()), None), candidates


def find_yandex() -> tuple[Path | None, list[Path]]:
    candidates: list[Path] = []
    for variable in ("LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)"):
        base = os.environ.get(variable)
        if base:
            candidates.append(Path(base) / "Yandex" / "YandexBrowser" / "Application" / "browser.exe")
    return next((path for path in candidates if path.is_file()), None), candidates


def open_chrome(chrome: Path) -> None:
    # Passing the URL to an existing Chrome installation opens a new tab when
    # Chrome is already running. No other browser process is touched.
    subprocess.Popen([str(chrome), URL], close_fds=True)


def create_test_audio() -> None:
    if TEST_AUDIO.is_file():
        return
    if sys.platform != "win32":
        raise RuntimeError("Тестовый речевой WAV можно автоматически создать только в Windows.")

    AUDIO_DIR.mkdir(parents=True, exist_ok=True)
    text = (
        "Проверка автоматических субтитров Caption Bridge. "
        "Это тестовая речь на русском языке. "
        "Сейчас можно настроить размер, положение, шрифт и прозрачность оверлея. "
        "После настройки сохраните расположение под нужным названием. "
        "Если текста недостаточно, запустите тестовую речь ещё раз."
    )
    script = f"""
$voice = New-Object -ComObject SAPI.SpVoice
$russianVoices = $voice.GetVoices('Language=419', '')
if ($russianVoices.Count -gt 0) {{ $voice.Voice = $russianVoices.Item(0) }}
$stream = New-Object -ComObject SAPI.SpFileStream
$stream.Open('{str(TEST_AUDIO).replace("'", "''")}', 3, $false)
$voice.AudioOutputStream = $stream
[void]$voice.Speak('{text.replace("'", "''")}')
$stream.Close()
"""
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-EncodedCommand", encoded],
        capture_output=True,
        text=True,
        timeout=30,
    )
    if result.returncode != 0 or not TEST_AUDIO.is_file():
        detail = result.stderr.strip() or "PowerShell/SAPI не создал файл."
        raise RuntimeError(f"Не удалось создать audio/test_audio_ru.wav: {detail}")


def pcm_chunks(path: Path, chunk_ms: int = 200):
    """Yield (format, PCM bytes) pairs; replace this source with loopback capture later."""
    with wave.open(str(path), "rb") as wav:
        if wav.getcomptype() != "NONE" or wav.getsampwidth() != 2:
            raise ValueError("Тестовый источник должен быть несжатым 16-битным PCM WAV.")
        audio_format = {
            "type": "format",
            "sampleRate": wav.getframerate(),
            "channels": wav.getnchannels(),
            "sampleFormat": "s16le",
        }
        frames_per_chunk = max(1, wav.getframerate() * chunk_ms // 1000)
        while data := wav.readframes(frames_per_chunk):
            yield audio_format, data


async def ensure_capture_helper() -> Path:
    global _capture_build_lock
    if CAPTURE_EXE.is_file():
        return CAPTURE_EXE
    if sys.platform != "win32":
        raise RuntimeError("Захват звука процесса доступен только в Windows.")
    if shutil.which("dotnet") is None:
        raise RuntimeError("Не найден .NET 8 SDK, необходимый для сборки компонента захвата Windows.")

    if _capture_build_lock is None:
        _capture_build_lock = asyncio.Lock()
    async with _capture_build_lock:
        if CAPTURE_EXE.is_file():
            return CAPTURE_EXE
        print("Сборка компонента захвата звука Windows...", flush=True)
        process = await asyncio.create_subprocess_exec(
            "dotnet",
            "build",
            str(CAPTURE_PROJECT),
            "-c",
            "Release",
            "--nologo",
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        stdout, stderr = await process.communicate()
        if process.returncode != 0 or not CAPTURE_EXE.is_file():
            detail = (stderr or stdout).decode(errors="replace").strip()
            raise RuntimeError(f"Не удалось собрать компонент захвата: {detail}")
        print("Компонент захвата звука Windows готов.", flush=True)
    return CAPTURE_EXE


async def ensure_overlay_helper() -> Path:
    global _overlay_build_lock
    if OVERLAY_EXE.is_file():
        return OVERLAY_EXE
    if sys.platform != "win32":
        raise RuntimeError("Оверлей субтитров доступен только в Windows.")
    if shutil.which("dotnet") is None:
        raise RuntimeError("Не найден .NET 8 SDK, необходимый для сборки оверлея.")

    if _overlay_build_lock is None:
        _overlay_build_lock = asyncio.Lock()
    async with _overlay_build_lock:
        if OVERLAY_EXE.is_file():
            return OVERLAY_EXE
        print("Сборка окна оверлея...", flush=True)
        process = await asyncio.create_subprocess_exec(
            "dotnet",
            "build",
            str(OVERLAY_PROJECT),
            "-c",
            "Release",
            "--nologo",
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        stdout, stderr = await process.communicate()
        if process.returncode != 0 or not OVERLAY_EXE.is_file():
            detail = (stderr or stdout).decode(errors="replace").strip()
            raise RuntimeError(f"Не удалось собрать оверлей: {detail}")
        print("Окно оверлея готово.", flush=True)
    return OVERLAY_EXE


def write_overlay_settings(settings: dict[str, int | bool | str | None]) -> None:
    OVERLAY_SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = OVERLAY_SETTINGS_FILE.with_suffix(".tmp")
    temporary.write_text(json.dumps(settings, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, OVERLAY_SETTINGS_FILE)


def custom_font_options() -> list[dict[str, str]]:
    if not CUSTOM_FONTS_DIR.is_dir():
        return []
    return [
        {"id": f"custom:{path.name}", "label": path.stem, "fileName": path.name}
        for path in sorted(CUSTOM_FONTS_DIR.iterdir(), key=lambda item: item.name.casefold())
        if path.is_file() and path.suffix.lower() in {".ttf", ".otf"}
    ]


async def available_custom_fonts(_request: web.Request) -> web.Response:
    return web.json_response({"fonts": custom_font_options()})


async def upload_custom_font(request: web.Request) -> web.Response:
    try:
        reader = await request.multipart()
        field = await reader.next()
        if field is None or field.name != "font" or not field.filename:
            raise ValueError("Выберите файл шрифта TTF или OTF.")

        original_name = Path(field.filename).name
        extension = Path(original_name).suffix.lower()
        if extension not in {".ttf", ".otf"}:
            raise ValueError("Поддерживаются только файлы .ttf и .otf.")
        safe_stem = "".join(
            character
            for character in Path(original_name).stem
            if character.isalnum() or character in " ._-"
        ).strip(" .")[:60] or "font"
        safe_name = safe_stem + extension

        contents = bytearray()
        while chunk := await field.read_chunk(size=64 * 1024):
            contents.extend(chunk)
            if len(contents) > 20 * 1024 * 1024:
                raise ValueError("Файл шрифта слишком большой; предел — 20 МБ.")
        if not contents:
            raise ValueError("Файл шрифта пуст.")

        CUSTOM_FONTS_DIR.mkdir(parents=True, exist_ok=True)
        destination = CUSTOM_FONTS_DIR / safe_name
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        temporary.write_bytes(contents)
        os.replace(temporary, destination)
        return web.json_response(
            {
                "ok": True,
                "font": {"id": f"custom:{safe_name}", "label": safe_stem, "fileName": safe_name},
                "fonts": custom_font_options(),
            }
        )
    except (AttributeError, OSError, TypeError, ValueError) as error:
        return web.json_response({"error": str(error)}, status=400)


async def active_audio_sessions(_request: web.Request) -> web.Response:
    try:
        helper = await ensure_capture_helper()
        process = await asyncio.create_subprocess_exec(
            str(helper),
            "list",
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=10)
        if process.returncode != 0:
            raise RuntimeError(stderr.decode(errors="replace").strip() or "Не удалось получить список аудиосессий.")
        return web.json_response({"sessions": json.loads(stdout)})
    except (OSError, RuntimeError, asyncio.TimeoutError, json.JSONDecodeError) as error:
        return web.json_response({"error": str(error)}, status=503)


async def caption_windows(_request: web.Request) -> web.Response:
    try:
        helper = await ensure_capture_helper()
        process = await asyncio.create_subprocess_exec(
            str(helper),
            "caption-windows",
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=5)
        if process.returncode != 0:
            raise RuntimeError(stderr.decode(errors="replace").strip() or "Не удалось найти окно субтитров.")
        return web.json_response({"windows": json.loads(stdout)})
    except (OSError, RuntimeError, asyncio.TimeoutError, json.JSONDecodeError) as error:
        return web.json_response({"error": str(error)}, status=503)


async def stop_overlay(app: web.Application) -> None:
    process = app[OVERLAY_STATE].get("process")
    if process and process.returncode is None:
        try:
            settings = json.loads(OVERLAY_SETTINGS_FILE.read_text(encoding="utf-8"))
            settings["nativeCaptionPlacement"] = "restore"
            write_overlay_settings(settings)
            await asyncio.sleep(0.35)
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            pass
        process.terminate()
        try:
            await asyncio.wait_for(process.wait(), timeout=3)
        except asyncio.TimeoutError:
            process.kill()
            await process.wait()
    app[OVERLAY_STATE]["process"] = None


async def overlay_settings(request: web.Request) -> web.Response:
    try:
        payload = await request.json()
        enabled = bool(payload.get("enabled"))
        if not enabled:
            await stop_overlay(request.app)
            print("Оверлей субтитров закрыт.", flush=True)
            return web.json_response({"ok": True, "enabled": False})

        width = int(payload.get("width"))
        height = int(payload.get("height"))
        font_size = int(payload.get("fontSize"))
        font_family = str(payload.get("fontFamily", "Chrome")).strip()
        opacity = int(payload.get("opacity"))
        reset_position = bool(payload.get("resetPosition"))
        x_value = payload.get("x")
        y_value = payload.get("y")
        x = int(x_value) if x_value is not None else None
        y = int(y_value) if y_value is not None else None
        click_through = bool(payload.get("clickThrough"))
        native_caption_placement = str(payload.get("nativeCaptionPlacement", "leave"))
        quiet = bool(payload.get("quiet"))
        if not 320 <= width <= 2400 or not 50 <= height <= 1400:
            raise ValueError("Допустимые размеры: ширина 320–2400, высота 50–1400.")
        if not 12 <= font_size <= 96:
            raise ValueError("Допустимый размер шрифта: 12–96.")
        allowed_fonts = {"Chrome", "Roboto", "Arial", "Segoe UI", "Consolas", "Tahoma", "Verdana"}
        font_file: str | None = None
        if font_family.startswith("custom:"):
            custom_name = font_family.removeprefix("custom:")
            custom_path = CUSTOM_FONTS_DIR / custom_name
            if (
                not custom_name
                or Path(custom_name).name != custom_name
                or custom_path.suffix.lower() not in {".ttf", ".otf"}
                or not custom_path.is_file()
            ):
                raise ValueError("Файл пользовательского шрифта не найден.")
            font_file = str(custom_path.resolve())
        elif font_family not in allowed_fonts:
            raise ValueError("Выбран неподдерживаемый шрифт.")
        if not 30 <= opacity <= 100:
            raise ValueError("Допустимая прозрачность: 30–100 процентов.")
        if x is not None and not -100_000 <= x <= 100_000:
            raise ValueError("Недопустимая координата X оверлея.")
        if y is not None and not -100_000 <= y <= 100_000:
            raise ValueError("Недопустимая координата Y оверлея.")
        if native_caption_placement not in {"leave", "edge-right", "offscreen-right", "restore"}:
            raise ValueError("Недопустимое положение штатного окна Chrome.")
        settings = {
            "width": width,
            "height": height,
            "fontSize": font_size,
            "fontFamily": font_family,
            "fontFile": font_file,
            "opacity": opacity,
            "resetPosition": reset_position,
            "x": x,
            "y": y,
            "clickThrough": click_through,
            "nativeCaptionPlacement": native_caption_placement,
        }
        write_overlay_settings(settings)

        current_process = request.app[OVERLAY_STATE].get("process")
        if current_process and current_process.returncode is None:
            if not quiet:
                print(f"Настройки оверлея обновлены: {width}×{height}, шрифт {font_size}.", flush=True)
            return web.json_response({"ok": True, "enabled": True, "created": False, **settings})

        helper = await ensure_overlay_helper()
        process = await asyncio.create_subprocess_exec(
            str(helper),
            "run",
            str(OVERLAY_SETTINGS_FILE),
            str(OVERLAY_WINDOW_STATE_FILE),
            stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        request.app[OVERLAY_STATE]["process"] = process
        await asyncio.sleep(0.2)
        if process.returncode is not None:
            raise RuntimeError("Оверлей не смог запуститься.")
        print(f"Оверлей субтитров открыт: {width}×{height}, шрифт {font_size}.", flush=True)
        return web.json_response(
            {
                "ok": True,
                "enabled": True,
                "created": True,
                **settings,
            }
        )
    except (AttributeError, TypeError, ValueError, OSError, RuntimeError) as error:
        return web.json_response({"error": str(error)}, status=400)


async def overlay_window_state(request: web.Request) -> web.Response:
    process = request.app[OVERLAY_STATE].get("process")
    result: dict[str, int | bool] = {
        "enabled": bool(process and process.returncode is None),
    }
    try:
        state = json.loads(OVERLAY_WINDOW_STATE_FILE.read_text(encoding="utf-8"))
        for key in ("x", "y", "width", "height"):
            value = state.get(key)
            if isinstance(value, int):
                result[key] = value
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        pass
    return web.json_response(result)


async def index(_request: web.Request) -> web.FileResponse:
    return web.FileResponse(WEB_DIR / "index.html", headers={"Cache-Control": "no-store"})


async def status(_request: web.Request) -> web.Response:
    chrome, checked = find_chrome()
    return web.json_response(
        {
            "chromeFound": chrome is not None,
            "chromePath": str(chrome) if chrome else None,
            "checkedPaths": [str(path) for path in checked],
        }
    )


async def open_in_chrome(_request: web.Request) -> web.Response:
    chrome, checked = find_chrome()
    if chrome is None:
        return web.json_response(
            {"ok": False, "error": "Google Chrome не найден.", "checkedPaths": [str(path) for path in checked]},
            status=404,
        )
    open_chrome(chrome)
    print("Открытие Chrome...", flush=True)
    return web.json_response({"ok": True})


async def open_in_yandex(_request: web.Request) -> web.Response:
    browser, checked = find_yandex()
    if browser is None:
        return web.json_response(
            {"ok": False, "error": "Яндекс Браузер не найден.", "checkedPaths": [str(path) for path in checked]},
            status=404,
        )
    subprocess.Popen([str(browser), URL], close_fds=True)
    print("Открытие экспериментальной страницы в Яндекс Браузере...", flush=True)
    return web.json_response({"ok": True})


async def client_event(request: web.Request) -> web.Response:
    try:
        event = (await request.json()).get("event")
    except (json.JSONDecodeError, AttributeError):
        raise web.HTTPBadRequest(text="Некорректный JSON")
    messages = {
        "audio-started": "Аудиотест запущен.",
        "audio-stopped": "Аудиотест остановлен.",
    }
    if event not in messages:
        raise web.HTTPBadRequest(text="Неизвестное событие")
    print(messages[event], flush=True)
    return web.json_response({"ok": True})


async def control_websocket(request: web.Request) -> web.WebSocketResponse:
    ws = web.WebSocketResponse(heartbeat=10)
    await ws.prepare(request)
    state = request.app[CONTROL_STATE]
    state["clients"].add(ws)

    pending_shutdown = state.get("shutdown_task")
    if pending_shutdown and not pending_shutdown.done():
        pending_shutdown.cancel()
    state["shutdown_task"] = None

    try:
        async for _message in ws:
            pass
    finally:
        state["clients"].discard(ws)
        if not state["clients"] and not request.app[STOP_EVENT].is_set():
            async def stop_after_grace_period() -> None:
                try:
                    await asyncio.sleep(3)
                    if not state["clients"]:
                        print("Последняя вкладка CaptionBridge закрыта. Завершение работы...", flush=True)
                        request.app[STOP_EVENT].set()
                except asyncio.CancelledError:
                    pass

            state["shutdown_task"] = asyncio.create_task(stop_after_grace_period())
    return ws


async def cleanup_control_websocket(app: web.Application) -> None:
    state = app[CONTROL_STATE]
    task = state.get("shutdown_task")
    if task and not task.done():
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
    await stop_overlay(app)


async def audio_websocket(request: web.Request) -> web.WebSocketResponse:
    ws = web.WebSocketResponse(heartbeat=20)
    await ws.prepare(request)
    print("Клиент подключён.", flush=True)
    streaming_task: asyncio.Task | None = None

    async def stream_audio() -> None:
        print("Аудиотест запущен.", flush=True)
        try:
            iterator = iter(pcm_chunks(TEST_AUDIO))
            first = True
            for audio_format, data in iterator:
                if first:
                    await ws.send_json(audio_format)
                    first = False
                await ws.send_bytes(data)
                frames = len(data) // (2 * audio_format["channels"])
                await asyncio.sleep(frames / audio_format["sampleRate"])
            await ws.send_json({"type": "end"})
        except asyncio.CancelledError:
            raise
        except (ConnectionResetError, RuntimeError):
            pass
        finally:
            print("Аудиотест остановлен.", flush=True)

    async def stream_process(pid: int) -> None:
        helper_process: asyncio.subprocess.Process | None = None
        print(f"Захват звука процесса запущен: PID {pid}.", flush=True)
        try:
            helper = await ensure_capture_helper()
            helper_process = await asyncio.create_subprocess_exec(
                str(helper),
                "capture",
                str(pid),
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            await ws.send_json(
                {"type": "format", "sampleRate": 44100, "channels": 2, "sampleFormat": "s16le"}
            )
            assert helper_process.stdout is not None
            while data := await helper_process.stdout.read(17640):
                aligned_size = len(data) - (len(data) % 4)
                if aligned_size:
                    await ws.send_bytes(data[:aligned_size])
            await helper_process.wait()
            if helper_process.returncode != 0:
                assert helper_process.stderr is not None
                detail = (await helper_process.stderr.read()).decode(errors="replace").strip()
                await ws.send_json({"type": "error", "message": detail or "Захват звука Windows остановлен."})
            else:
                await ws.send_json({"type": "end"})
        except asyncio.CancelledError:
            raise
        except (ConnectionResetError, OSError, RuntimeError) as error:
            if not ws.closed:
                await ws.send_json({"type": "error", "message": str(error)})
        finally:
            if helper_process and helper_process.returncode is None:
                helper_process.terminate()
                try:
                    await asyncio.wait_for(helper_process.communicate(), timeout=3)
                except asyncio.TimeoutError:
                    helper_process.kill()
                    await helper_process.communicate()
            print(f"Захват звука процесса остановлен: PID {pid}.", flush=True)

    try:
        async for message in ws:
            if message.type != WSMsgType.TEXT:
                continue
            try:
                command = json.loads(message.data).get("command")
            except (json.JSONDecodeError, AttributeError):
                continue
            if command == "start":
                if streaming_task and not streaming_task.done():
                    streaming_task.cancel()
                    await asyncio.gather(streaming_task, return_exceptions=True)
                streaming_task = asyncio.create_task(stream_audio())
            elif command == "capture-process":
                try:
                    pid = int(json.loads(message.data).get("pid"))
                    if pid <= 0:
                        raise ValueError
                except (TypeError, ValueError):
                    await ws.send_json({"type": "error", "message": "Некорректный идентификатор процесса."})
                    continue
                if streaming_task and not streaming_task.done():
                    streaming_task.cancel()
                    await asyncio.gather(streaming_task, return_exceptions=True)
                streaming_task = asyncio.create_task(stream_process(pid))
            elif command == "stop" and streaming_task and not streaming_task.done():
                streaming_task.cancel()
                await asyncio.gather(streaming_task, return_exceptions=True)
    finally:
        if streaming_task and not streaming_task.done():
            streaming_task.cancel()
            await asyncio.gather(streaming_task, return_exceptions=True)
    return ws


def make_app() -> web.Application:
    app = web.Application(client_max_size=21 * 1024**2)
    app[STOP_EVENT] = asyncio.Event()
    app[CONTROL_STATE] = {"clients": set(), "shutdown_task": None}
    app[OVERLAY_STATE] = {"process": None}
    app.on_cleanup.append(cleanup_control_websocket)
    app.router.add_get("/", index)
    app.router.add_get("/api/status", status)
    app.router.add_get("/api/sessions", active_audio_sessions)
    app.router.add_get("/api/caption-windows", caption_windows)
    app.router.add_get("/api/overlay-state", overlay_window_state)
    app.router.add_get("/api/fonts", available_custom_fonts)
    app.router.add_post("/api/fonts", upload_custom_font)
    app.router.add_post("/api/overlay-settings", overlay_settings)
    app.router.add_post("/api/open-chrome", open_in_chrome)
    app.router.add_post("/api/open-yandex", open_in_yandex)
    app.router.add_post("/api/event", client_event)
    app.router.add_get("/ws/control", control_websocket)
    app.router.add_get("/ws/audio", audio_websocket)
    app.router.add_static("/web", WEB_DIR, show_index=False)
    app.router.add_static("/audio", AUDIO_DIR, show_index=False)
    return app


async def run() -> None:
    create_test_audio()
    chrome, checked = find_chrome()

    app = make_app()
    runner = web.AppRunner(app, access_log=None)
    await runner.setup()
    site = web.TCPSite(runner, HOST, PORT)
    await site.start()

    print("CaptionBridge запущен", flush=True)
    print(f"Сервер: {URL}\n", flush=True)
    if chrome:
        print(f"Chrome найден:\n{chrome}\n", flush=True)
        print("ВНИМАНИЕ: сейчас откроется новая вкладка Chrome...\n", flush=True)
        open_chrome(chrome)
    else:
        print("Google Chrome не найден. Проверены пути:", flush=True)
        for path in checked:
            print(f"- {path}", flush=True)
        print(f"\nПосле установки Chrome откройте {URL}.", flush=True)

    try:
        await app[STOP_EVENT].wait()
    finally:
        await runner.cleanup()
    print("CaptionBridge остановлен.", flush=True)


if __name__ == "__main__":
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        print("\nCaptionBridge остановлен.")
    except (OSError, RuntimeError) as error:
        print(f"Не удалось запустить CaptionBridge: {error}", file=sys.stderr)
        raise SystemExit(1)
