# Разработка CaptionBridge

## Структура

- `main.py` — исследовательский сервер с диагностикой и Яндекс-экспериментом.
- `prerelease.py` — пользовательская точка запуска без экспериментальных маршрутов.
- `web/index.html` — исследовательский интерфейс.
- `web/final.html` — предрелизный интерфейс.
- `capture_helper/` — .NET-компонент захвата аудио выбранного процесса.
- `overlay_helper/` — .NET-компонент чтения Chrome Live Caption и показа оверлея.
- `build_prerelease.ps1` — воспроизводимая сборка EXE для Windows x64.

## Запуск из исходников

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

Исследовательская версия:

```powershell
python main.py
```

Предрелизная версия:

```powershell
python prerelease.py
```

Обе версии используют `127.0.0.1:17321` и одновременно не запускаются.

## Сборка EXE

```powershell
.\build_prerelease.ps1
```

Сценарий:

1. создаёт локальное окружение `.build-venv`, если оно отсутствует;
2. устанавливает PyInstaller и зависимости;
3. публикует оба .NET-проекта для `win-x64` в self-contained режиме;
4. объединяет Python, .NET runtime, веб-ресурсы и проверочную запись в один EXE.

Результат находится в `dist\CaptionBridge-Prerelease.exe`.

Безопасная проверка содержимого, не запускающая сервер или Chrome:

```powershell
.\dist\CaptionBridge-Prerelease.exe --self-check
```

## Проверки перед релизом

```powershell
python -m py_compile main.py prerelease.py
node --check web/app.js
node --check web/final.js
dotnet build capture_helper/CaptionBridge.Capture.csproj -c Release
dotnet build overlay_helper/CaptionBridge.Overlay.csproj -c Release
```

Обычный smoke-тест EXE открывает Chrome, поэтому перед ним пользователь должен быть предупреждён.
