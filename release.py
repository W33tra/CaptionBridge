from __future__ import annotations

import asyncio
import sys

import prerelease as app


VERSION = "0.0.1"
app.APP_LABEL = f"версия {VERSION}"


if __name__ == "__main__":
    if "--self-check" in sys.argv:
        result = app.self_check()
        if result == 0:
            print(f"VERSION={VERSION}")
        raise SystemExit(result)
    try:
        asyncio.run(app.run())
    except KeyboardInterrupt:
        print(f"\nCaptionBridge — версия {VERSION} остановлена.")
    except (OSError, RuntimeError) as error:
        print(f"Не удалось запустить CaptionBridge: {error}", file=sys.stderr)
        raise SystemExit(1)
