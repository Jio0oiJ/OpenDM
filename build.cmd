@echo off
setlocal
where dotnet >nul 2>nul
if errorlevel 1 (
  set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
) else (
  set "DOTNET=dotnet"
)

echo [1/2] 编译...
"%DOTNET%" build OpenDM.sln -c Release --nologo || goto :fail

echo [2/2] 发布单文件 exe...
"%DOTNET%" publish src\OpenDM.App\OpenDM.App.csproj -c Release -r win-x64 ^
  --self-contained false -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist --nologo || goto :fail

echo.
echo 完成: dist\OpenDM.exe
goto :eof

:fail
echo.
echo 构建失败。
exit /b 1
