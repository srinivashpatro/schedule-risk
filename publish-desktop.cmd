@echo off
rem Build the Windows app: publish\desktop\ProjectRiskAnalysis.exe with its wwwroot folder, and ProjectRiskAnalysis-windows.zip.
rem The .exe includes .NET, so nothing needs installing; it uses Microsoft Edge WebView2, which Windows 10 and 11 already have.
if exist publish\desktop rmdir /s /q publish\desktop
dotnet publish src\ScheduleRisk.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\desktop || exit /b 1
powershell -NoProfile -Command "Compress-Archive -Path publish\desktop\* -DestinationPath publish\ProjectRiskAnalysis-windows.zip -Force" || exit /b 1
echo.
echo Windows app written to %cd%\publish\ProjectRiskAnalysis-windows.zip
