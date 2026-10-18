@echo off
rem Copyright (c) 2026 Falcon 9.
rem This work is licensed under the GNU General Public License v3.0.
rem To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html
rem Builds ..\SolSatellite\Plugins\SolSatellite.dll (next to this src folder) against the game's
rem own assemblies. Needs the .NET SDK (any version) for its C# compiler and KSP_DIR set to the
rem game folder, e.g.  set "KSP_DIR=D:\Games\Kerbal Space Program"  (with Sol and the mods SolSat uses).
rem An output path may be passed, e.g. build.bat "%KSP_DIR%\GameData\SolSatellite\Plugins\SolSatellite.dll"
rem (close KSP first: it locks the DLL).
rem A portable .pdb lands next to each DLL: file:line in stack traces and breakpoints with the development player
rem (KSP_DevMode_On.bat in the game folder); the release player ignores it.
setlocal
set "KSP=%KSP_DIR%"
if not defined KSP (echo Set KSP_DIR to the KSP folder first, e.g. set "KSP_DIR=D:\Games\Kerbal Space Program" & exit /b 1)
if not exist "%KSP%\KSP_x64_Data\Managed\Assembly-CSharp.dll" (echo KSP not found in "%KSP%" & exit /b 1)
set "MANAGED=%KSP%\KSP_x64_Data\Managed"
set "GD=%KSP%\GameData"
set "OUT=%~1"
if not defined OUT set "OUT=%~dp0..\SolSatellite\Plugins\SolSatellite.dll"
set "CSC="
for /d %%d in ("%ProgramFiles%\dotnet\sdk\*") do if exist "%%d\Roslyn\bincore\csc.dll" set "CSC=%%d\Roslyn\bincore\csc.dll"
if not defined CSC (echo .NET SDK not found: https://dotnet.microsoft.com/download & exit /b 1)
for %%f in ("%OUT%") do if not exist "%%~dpf" mkdir "%%~dpf"
dotnet "%CSC%" -nologo -noconfig -nostdlib+ -target:library -optimize+ -debug:portable -unsafe+ -langversion:9 ^
  -out:"%OUT%" ^
  -r:"%MANAGED%\mscorlib.dll" -r:"%MANAGED%\System.dll" -r:"%MANAGED%\System.Core.dll" ^
  -r:"%MANAGED%\UnityEngine.dll" -r:"%MANAGED%\UnityEngine.CoreModule.dll" -r:"%MANAGED%\Assembly-CSharp.dll" ^
  -r:"%MANAGED%\UnityEngine.AssetBundleModule.dll" -r:"%MANAGED%\UnityEngine.UnityWebRequestModule.dll" ^
  -r:"%MANAGED%\UnityEngine.UnityWebRequestTextureModule.dll" -r:"%MANAGED%\UnityEngine.IMGUIModule.dll" ^
  -r:"%MANAGED%\UnityEngine.TextRenderingModule.dll" -r:"%MANAGED%\UnityEngine.AnimationModule.dll" ^
  -r:"%MANAGED%\UnityEngine.UIModule.dll" -r:"%MANAGED%\UnityEngine.UI.dll" -r:"%MANAGED%\UnityEngine.PhysicsModule.dll" ^
  -r:"%MANAGED%\UnityEngine.ImageConversionModule.dll" ^
  -r:"%GD%\KSPTextureLoader\Plugins\KSPTextureLoader.dll" -r:"%GD%\ParallaxContinued\Plugins\ParallaxContinued.dll" ^
  -r:"%GD%\000_Harmony\0Harmony.dll" ^
  "%~dp0*.cs"
if errorlevel 1 exit /b 1
rem The BurstPQS adapter of the real-terrain PQSMod, next to the plugin (KSP skips it without BurstPQS).
if not exist "%GD%\BurstPQS\Plugins\BurstPQS.dll" (echo BurstPQS not installed: adapter not built & exit /b 0)
for %%f in ("%OUT%") do set "ADAPTER=%%~dpfSolSatellite.BurstPQS.dll"
dotnet "%CSC%" -nologo -noconfig -nostdlib+ -target:library -optimize+ -debug:portable -unsafe+ -langversion:9 ^
  -out:"%ADAPTER%" ^
  -r:"%MANAGED%\mscorlib.dll" -r:"%MANAGED%\System.dll" -r:"%MANAGED%\System.Core.dll" ^
  -r:"%MANAGED%\UnityEngine.dll" -r:"%MANAGED%\UnityEngine.CoreModule.dll" -r:"%MANAGED%\Assembly-CSharp.dll" ^
  -r:"%OUT%" -r:"%GD%\BurstPQS\Plugins\BurstPQS.dll" ^
  -r:"%GD%\000_KSPBurst\Plugins\Unity.Burst.dll" -r:"%GD%\000_KSPBurst\Plugins\Unity.Collections.dll" ^
  -r:"%GD%\000_KSPBurst\Plugins\Unity.Mathematics.dll" -r:"%GD%\000_KSPBurst\Plugins\System.Runtime.CompilerServices.Unsafe.dll" ^
  "%~dp0BurstPQS\*.cs"
