@echo off
rem Builds and runs the demo database loader; every argument is passed on to it
rem (--recreate, --dry-run, --no-bson, ...; see loader\Options.cs).
rem
rem The connection string comes from Informix.demo\.env (INFORMIX_DEMO_CONNECTION; copy .env.example)
rem or from an environment variable of that name; the loader reads the file itself.
setlocal
set "HERE=%~dp0"
dotnet run --project "%HERE%loader" -c Release -- %*
exit /b %ERRORLEVEL%
