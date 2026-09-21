@echo off
rem Produce a static website in publish\wwwroot. Copy that folder to any static host
rem (IIS, SharePoint site assets, Azure Static Web Apps, Netlify, GitHub Pages...). No server code needed.
dotnet publish src\ScheduleRisk.Web -c Release -o publish || exit /b 1
echo.
echo Static site written to %cd%\publish\wwwroot
