@echo off
rem Build an offline copy of the browser app: publish\offline\ and ScheduleRisk-offline.zip.
rem Unzip on any Windows PC and double-click Start-Offline.cmd; no internet or install needed.
dotnet publish src\ScheduleRisk.Web -c Release -o publish || exit /b 1
if exist publish\offline rmdir /s /q publish\offline
xcopy /e /i /q publish\wwwroot publish\offline\wwwroot >nul || exit /b 1
copy /y tools\offline\serve.ps1 publish\offline\ >nul
copy /y tools\offline\Start-Offline.cmd publish\offline\ >nul
copy /y tools\offline\README-offline.txt publish\offline\ >nul
powershell -NoProfile -Command "Compress-Archive -Path publish\offline\* -DestinationPath publish\ScheduleRisk-offline.zip -Force" || exit /b 1
echo.
echo Offline copy written to %cd%\publish\ScheduleRisk-offline.zip
