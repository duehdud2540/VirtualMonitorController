@echo off
setlocal
cd /d "%~dp0"
if not defined VCINSTALLDIR (
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VM_VS=%%i"
  if not defined VM_VS (
    echo Visual Studio C++ tools were not found. Install the Desktop development with C++ workload.
    exit /b 1
  )
)
if not defined VCINSTALLDIR call "%VM_VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /W4 /O2 /MT /LD /Fo"NativeCapture\Capture.obj" /Fe"NativeCapture\VirtualMonitor.Capture.dll" "NativeCapture\Capture.cpp" /link d3d11.lib windowsapp.lib /IMPLIB:"NativeCapture\VirtualMonitor.Capture.lib"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /W4 /O2 /MT /LD /Fo"NativePreview\Renderer.obj" /Fe"NativePreview\VirtualMonitor.Preview.dll" "NativePreview\Renderer.cpp" /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib /IMPLIB:"NativePreview\VirtualMonitor.Preview.lib"
exit /b %errorlevel%
