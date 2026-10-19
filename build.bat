@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler.
  exit /b 1
)
"%CSC%" /nologo /optimize+ /target:winexe /win32icon:assets\icon.ico /out:VidPaper.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\VidPaper.cs
if errorlevel 1 (
  echo Build failed.
  if not defined CI pause
  exit /b 1
)
echo Built VidPaper.exe
if not defined CI pause
