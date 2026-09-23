@echo off
cd /d "%~dp0"
set FW=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" -nologo -optimize -unsafe -target:winexe -out:WindowGlass.exe -win32manifest:WindowGlass.manifest -win32icon:WindowGlass.ico -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll -r:"%FW%\WPF\UIAutomationClient.dll" -r:"%FW%\WPF\UIAutomationTypes.dll" -r:"%FW%\WPF\WindowsBase.dll" -r:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd" -r:"%FW%\System.Runtime.WindowsRuntime.dll" -r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" -r:"%FW%\System.Runtime.dll" WindowGlass.cs
