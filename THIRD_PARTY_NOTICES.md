# Сторонние компоненты и уведомления

CaptionBridge — независимый проект, не связанный с Google LLC или Яндексом и не одобренный ими. Google Chrome, Chrome, Live Caption, Яндекс и Яндекс Браузер являются обозначениями соответствующих правообладателей. Браузеры, их языковые модели и другие файлы правообладателей не входят в CaptionBridge.

Релизный EXE содержит или использует следующие распространяемые компоненты. Полные тексты их лицензий находятся в каталоге [`licenses`](licenses/).

| Компонент | Версия | Лицензия | Файл |
| --- | --- | --- | --- |
| Python | 3.13 | PSF License и включённые уведомления | `licenses/Python-3.13-LICENSE.txt` |
| aiohttp | 3.14.3 | Apache-2.0 AND MIT | `licenses/aiohttp-3.14.3-LICENSE.txt` |
| aiohappyeyeballs | 2.7.1 | PSF-2.0 | `licenses/aiohappyeyeballs-2.7.1-LICENSE.txt` |
| aiosignal | 1.4.0 | Apache-2.0 | `licenses/aiosignal-1.4.0-LICENSE.txt` |
| attrs | 26.1.0 | MIT | `licenses/attrs-26.1.0-LICENSE.txt` |
| frozenlist | 1.8.0 | Apache-2.0 | `licenses/frozenlist-1.8.0-LICENSE.txt` |
| idna | 3.19 | BSD-3-Clause | `licenses/idna-3.19-LICENSE.md` |
| multidict | 6.8.0 | Apache-2.0 | `licenses/multidict-6.8.0-LICENSE.txt` |
| propcache | 0.5.2 | Apache-2.0 | `licenses/propcache-0.5.2-LICENSE.txt`, `licenses/propcache-0.5.2-NOTICE.txt` |
| yarl | 1.24.5 | Apache-2.0 | `licenses/yarl-1.24.5-LICENSE.txt`, `licenses/yarl-1.24.5-NOTICE.txt` |
| PyInstaller bootloader | 6.22.2 | GPL-2.0-or-later с Bootloader Exception | `licenses/PyInstaller-6.22.2-COPYING.txt` |
| Microsoft .NET | 8, self-contained runtime | условия и сторонние уведомления Microsoft | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-ThirdPartyNotices.txt` |

PyInstaller используется только как инструмент сборки. Его Bootloader Exception разрешает распространять создаваемый объединённый исполняемый файл без распространения самого приложения под GPL PyInstaller. Сам CaptionBridge независимо выпускается под `GPL-3.0-or-later`.

Файлы сборочных инструментов, которые не попадают в итоговый EXE, не являются частью пользовательского дистрибутива.
