@echo off
rem Builds bin\BloxNest.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Run make-icon.ps1 first if the logo in MainWindow.xaml changed.
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%~dp0..\bin" mkdir "%~dp0..\bin"
"%FW%\csc.exe" /nologo /target:winexe /optimize ^
  /win32icon:"%~dp0icon.ico" ^
  /resource:"%~dp0MainWindow.xaml",MainWindow.xaml ^
  /resource:"%~dp0icon.ico",icon.ico ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:"%~dp0..\bin\BloxNest.exe" "%~dp0Program.cs"
