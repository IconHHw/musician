@echo off
rem ============================================================
rem  Musician 环境恢复 —— 转发给 tools\setup.sh
rem
rem  真正的脚本是 setup.sh（bash）。这里只是个入口，
rem  让习惯用 cmd / 双击的人也能直接跑。
rem
rem  为什么真正的脚本用 bash 而不是 .cmd 或 .ps1：
rem    - .cmd  在中文 Windows 上有代码页问题，UTF-8 中文会变乱码
rem    - .ps1  Windows 默认禁止运行脚本，每个人第一次都要先改执行策略
rem    - bash  由 Git for Windows 自带，两者问题都没有
rem
rem  本文件刻意只用 ASCII 字符 —— 中文放到 setup.sh 里去输出。
rem ============================================================

setlocal

where bash >nul 2>&1
if errorlevel 1 (
    echo.
    echo   [ERROR] bash not found.
    echo           bash ships with Git for Windows. Install Git from:
    echo             https://git-scm.com/download/win
    echo           ...or use PowerShell instead:
    echo             wsl bash tools/setup.sh
    echo.
    exit /b 1
)

bash "%~dp0setup.sh"
exit /b %errorlevel%
