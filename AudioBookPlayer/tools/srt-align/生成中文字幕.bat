@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist ".venv\Scripts\pythonw.exe" (
  echo.
  echo   还没装运行环境。
  echo   请按 README 的"可选：句向量语义对齐"三条命令装一次，
  echo   或者先用 README 里的命令行方式跑（纯长度对齐，零依赖）。
  echo.
  pause
  exit /b 1
)
start "" ".venv\Scripts\pythonw.exe" "%~dp0gui.py" %*
exit /b 0