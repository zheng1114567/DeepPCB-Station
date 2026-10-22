@echo off
rem DeepPCB-Station 上位机（WPF 新 UI）启动器：cwd 必须是仓库根
rem 先编译后启动，杜绝“改了源码却跑旧 exe”的指向性/陈旧二进制问题。
cd /d E:\demo\DeepPCB-Station

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [启动器] 未在 PATH 找到 dotnet，直接启动已有 exe。
  goto launch
)

echo [启动器] 正在编译 Debug 版本（如有报错会暂停显示）...
dotnet build src\DeepPCBStation\DeepPCBStation.csproj -c Debug
if errorlevel 1 (
  echo [启动器] 编译失败，仍尝试启动已有 exe（可能不是最新 UI）。
  pause
)

:launch
start "" "%~dp0src\DeepPCBStation\bin\Debug\net48-windows\DeepPCBStation.exe"
