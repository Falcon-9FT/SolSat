@echo off
rem Copyright (c) 2026 Falcon 9.
rem This work is licensed under the GNU General Public License v3.0.
rem To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html
rem Builds ..\SolSatellite\Shaders\ParallaxTerrain-windows.unity3d (Parallax's terrain
rem shaders plus the SolSatellite changes) and SolSatellite-windows.unity3d (the plugin's own
rem shaders) from Shaders\. Needs Unity 2019.4.18f1, the version KSP uses.
rem   build_shaders.bat       both bundles (takes a few minutes)
rem   build_shaders.bat own   only SolSatellite-windows.unity3d (quick)
setlocal
set "UNITY=C:\Program Files\Unity\Editor\Unity.exe"
if not exist "%UNITY%" set "UNITY=C:\Program Files\Unity\Hub\Editor\2019.4.18f1\Editor\Unity.exe"
if not exist "%UNITY%" (echo Unity 2019.4.18f1 not found & exit /b 1)
set "PROJECT=%~dp0ShaderProject"
set "METHOD=SolSatBuild.BuildTerrainBundle"
if /i "%~1"=="own" set "METHOD=SolSatBuild.BuildOwnBundle"
if exist "%PROJECT%\Build" rmdir /s /q "%PROJECT%\Build"
robocopy "%~dp0Shaders" "%PROJECT%\Assets\Shaders" /MIR /NJH /NJS /NFL /NDL >nul
if errorlevel 8 (echo Copying shaders failed & exit /b 1)
echo Building shader bundles (%METHOD%)...
"%UNITY%" -batchmode -nographics -quit -projectPath "%PROJECT%" -executeMethod %METHOD% -logFile "%~dp0build_shaders.log"
set "BUILD_ERROR=%ERRORLEVEL%"
rem Unity's Library holds ~20k shader-cache .bin files, and Kopernicus tries to load every .bin
rem under GameData at startup (tens of minutes). Always drop it; the next build recreates it.
for %%d in (Library Temp Logs obj) do if exist "%PROJECT%\%%d" rmdir /s /q "%PROJECT%\%%d"
if not "%BUILD_ERROR%"=="0" (echo Build failed, see build_shaders.log & exit /b 1)
set "SHADERS=%~dp0..\SolSatellite\Shaders"
if not exist "%SHADERS%" mkdir "%SHADERS%"
if exist "%PROJECT%\Build\parallaxterrain-windows.unity3d" (
  copy /y "%PROJECT%\Build\parallaxterrain-windows.unity3d" "%SHADERS%\ParallaxTerrain-windows.unity3d" >nul || goto locked
)
copy /y "%PROJECT%\Build\solsatellite-windows.unity3d" "%SHADERS%\SolSatellite-windows.unity3d" >nul || goto locked
echo Done: bundles copied to ..\SolSatellite\Shaders
exit /b 0
:locked
echo Built, but the bundles in ..\SolSatellite\Shaders are in use (KSP running?).
echo Copy ShaderProject\Build\*.unity3d there after closing KSP.
exit /b 2
