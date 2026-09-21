@echo off
rem Build the solution and run all tests (needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0)
dotnet --version || (echo .NET 8 SDK not found & exit /b 1)
dotnet build ScheduleRisk.sln -c Release || exit /b 1
dotnet test ScheduleRisk.sln -c Release --no-build --logger "console;verbosity=normal" || exit /b 1
echo.
echo Try it:
echo   dotnet run --project src\ScheduleRisk.Cli -c Release -- simulate testdata\synth_500.xer --risk testdata\synth_500.risk.json --out sra-output
