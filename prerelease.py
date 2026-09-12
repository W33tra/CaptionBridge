from __future__ import annotations

import asyncio
import base64
import subprocess
import sys

from aiohttp import web

import main as core


CAPTION_CHECK_AUDIO = core.AUDIO_DIR / "caption_check_ru.wav"
BUNDLED_NATIVE_DIR = core.ROOT / "native"
APP_LABEL = "предрелизная версия"
LEGAL_DOCUMENTS = {
    "license": core.ROOT / "LICENSE",
    "notices": core.ROOT / "THIRD_PARTY_NOTICES.md",
    "privacy": core.ROOT / "PRIVACY.md",
    "contributing": core.ROOT / "CONTRIBUTING.md",
}
LICENSES_DIR = core.ROOT / "licenses"

if (BUNDLED_NATIVE_DIR.is_dir()):
    core.CAPTURE_EXE = BUNDLED_NATIVE_DIR / "CaptionBridge.Host.exe"
    core.OVERLAY_EXE = BUNDLED_NATIVE_DIR / "CaptionBridge.Overlay.exe"


def create_caption_check_audio() -> None:
    if CAPTION_CHECK_AUDIO.is_file():
        return
    if sys.platform != "win32":
        raise RuntimeError("Проверочную аудиозапись можно автоматически создать только в Windows.")

    core.AUDIO_DIR.mkdir(parents=True, exist_ok=True)
    text = (
        "Здравствуйте! Это проверочная аудиозапись. "
        "Если вы видите этот текст в окне субтитров, значит всё работает правильно."
    )
    script = f"""
$voice = New-Object -ComObject SAPI.SpVoice
$russianVoices = $voice.GetVoices('Language=419', '')
if ($russianVoices.Count -gt 0) {{ $voice.Voice = $russianVoices.Item(0) }}
$stream = New-Object -ComObject SAPI.SpFileStream
$stream.Open('{str(CAPTION_CHECK_AUDIO).replace("'", "''")}', 3, $false)
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
    if result.returncode != 0 or not CAPTION_CHECK_AUDIO.is_file():
        detail = result.stderr.strip() or "PowerShell/SAPI не создал файл."
        raise RuntimeError(f"Не удалось создать audio/caption_check_ru.wav: {detail}")


async def final_index(_request: web.Request) -> web.FileResponse:
    return web.FileResponse(
        core.WEB_DIR / "final.html",
        headers={"Cache-Control": "no-store"},
    )


async def legal_document(request: web.Request) -> web.Response:
    path = LEGAL_DOCUMENTS.get(request.match_info["document"])
    if path is None or not path.is_file():
        raise web.HTTPNotFound()
    return web.Response(
        text=path.read_text(encoding="utf-8"),
        content_type="text/plain",
        charset="utf-8",
        headers={"Cache-Control": "no-store"},
    )


def make_app() -> web.Application:
    app = web.Application(client_max_size=21 * 1024**2)
    app[core.STOP_EVENT] = asyncio.Event()
    app[core.CONTROL_STATE] = {"clients": set(), "shutdown_task": None}
    app[core.OVERLAY_STATE] = {"process": None}
    app.on_cleanup.append(core.cleanup_control_websocket)

    app.router.add_get("/", final_index)
    app.router.add_get("/legal/{document}", legal_document)
    app.router.add_get("/api/status", core.status)
    app.router.add_get("/api/sessions", core.active_audio_sessions)
    app.router.add_get("/api/caption-windows", core.caption_windows)
    app.router.add_get("/api/overlay-state", core.overlay_window_state)
    app.router.add_get("/api/fonts", core.available_custom_fonts)
    app.router.add_post("/api/fonts", core.upload_custom_font)
    app.router.add_post("/api/overlay-settings", core.overlay_settings)
    app.router.add_post("/api/open-chrome", core.open_in_chrome)
    app.router.add_post("/api/event", core.client_event)
    app.router.add_get("/ws/control", core.control_websocket)
    app.router.add_get("/ws/audio", core.audio_websocket)
    app.router.add_static("/web", core.WEB_DIR, show_index=False)
    app.router.add_static("/audio", core.AUDIO_DIR, show_index=False)
    app.router.add_static("/legal/licenses", LICENSES_DIR, show_index=False)
    return app


def self_check() -> int:
    required_files = (
        core.WEB_DIR / "final.html",
        core.WEB_DIR / "style.css",
        core.WEB_DIR / "final.css",
        core.WEB_DIR / "app.js",
        core.WEB_DIR / "final.js",
        CAPTION_CHECK_AUDIO,
        core.CAPTURE_EXE,
        core.OVERLAY_EXE,
        *LEGAL_DOCUMENTS.values(),
        LICENSES_DIR / "Python-3.13-LICENSE.txt",
        LICENSES_DIR / "aiohttp-3.14.3-LICENSE.txt",
        LICENSES_DIR / "PyInstaller-6.22.2-COPYING.txt",
        LICENSES_DIR / "dotnet-LICENSE.txt",
        LICENSES_DIR / "dotnet-ThirdPartyNotices.txt",
    )
    missing = [str(path) for path in required_files if not path.is_file()]
    if missing:
        print("SELF_CHECK_FAILED")
        for path in missing:
            print(f"MISSING: {path}")
        return 1

    app = make_app()
    route_paths = {route.resource.canonical for route in app.router.routes()}
    if "/api/open-yandex" in route_paths or "/" not in route_paths:
        print("SELF_CHECK_FAILED: invalid prerelease routes")
        return 1

    print("SELF_CHECK_OK")
    print(f"CAPTURE_HELPER={core.CAPTURE_EXE}")
    print(f"OVERLAY_HELPER={core.OVERLAY_EXE}")
    print(f"CAPTION_AUDIO={CAPTION_CHECK_AUDIO}")
    return 0


async def run() -> None:
    create_caption_check_audio()

    chrome, checked = core.find_chrome()
    app = make_app()
    runner = web.AppRunner(app, access_log=None)
    await runner.setup()
    site = web.TCPSite(runner, core.HOST, core.PORT)
    await site.start()

    print(f"CaptionBridge — {APP_LABEL} запущена", flush=True)
    print(f"Сервер: {core.URL}\n", flush=True)
    if chrome:
        print(f"Chrome найден:\n{chrome}\n", flush=True)
        print("ВНИМАНИЕ: сейчас откроется новая вкладка Chrome...\n", flush=True)
        core.open_chrome(chrome)
    else:
        print("Google Chrome не найден. Проверены пути:", flush=True)
        for path in checked:
            print(f"- {path}", flush=True)
        print(f"\nПосле установки Chrome откройте {core.URL}.", flush=True)

    try:
        await app[core.STOP_EVENT].wait()
    finally:
        await runner.cleanup()
    print(f"CaptionBridge — {APP_LABEL} остановлена.", flush=True)


if __name__ == "__main__":
    if "--self-check" in sys.argv:
        raise SystemExit(self_check())
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        print(f"\nCaptionBridge — {APP_LABEL} остановлена.")
    except (OSError, RuntimeError) as error:
        print(f"Не удалось запустить CaptionBridge: {error}", file=sys.stderr)
        raise SystemExit(1)
