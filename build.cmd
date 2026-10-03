@echo off
rem Builds ch397bridge.exe and force-winusb.exe into bin\ with the .NET Framework compiler included in Windows.
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /platform:anycpu
if not exist "%~dp0bin" mkdir "%~dp0bin"
%CSC% /target:winexe /out:"%~dp0bin\ch397bridge.exe" "%~dp0src\Program.cs" || exit /b 1
%CSC% /out:"%~dp0bin\force-winusb.exe" "%~dp0src\ForceWinUsb.cs" || exit /b 1
echo Built bin\ch397bridge.exe and bin\force-winusb.exe
