@echo off
REM Creates an EMPTY development database, applies the schema and every migration in order, then the dev seed.
REM Usage:  setup-db.bat [database_name]      (default: alrahma_dev)
REM Development and testing only: refuses alrahma_db, any name containing "prod", or ASPNETCORE_ENVIRONMENT=Production.
REM Needs PostgreSQL 13+ (found by ..\pg-env.bat; set PGBIN / PGPORT / PGPASSWORD in ..\local-env.bat if needed).
setlocal EnableDelayedExpansion
cd /d %~dp0
set "DB=%~1"
if "%DB%"=="" set "DB=alrahma_dev"
if /i "%ASPNETCORE_ENVIRONMENT%"=="Production" (echo  Refused: ASPNETCORE_ENVIRONMENT=Production.& exit /b 1)
if /i "%DB%"=="alrahma_db" (echo  Refused: alrahma_db is the stable database.& exit /b 1)
echo %DB% | findstr /i "prod" >nul && (echo  Refused: "%DB%" looks like a production database.& exit /b 1)
if exist "%~dp0..\pg-env.bat" (call "%~dp0..\pg-env.bat" || exit /b 1) else (if not defined PGHOST set "PGHOST=localhost"& if not defined PGUSER set "PGUSER=postgres"& if not defined PGPORT set "PGPORT=5432")
set "PGCLIENTENCODING=UTF8"
set "PGOPTIONS=-c client_min_messages=warning"
set "PSQL=psql"
if defined PGBIN set "PSQL=%PGBIN%\psql.exe"

"%PSQL%" -w -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='%DB%'" | findstr 1 >nul && (echo  Refused: database %DB% already exists. Drop it first or choose another name.& exit /b 1)
echo [1/4] Creating database %DB% ...
"%PSQL%" -w -d postgres -q -v ON_ERROR_STOP=1 -c "CREATE DATABASE %DB% ENCODING 'UTF8' TEMPLATE template0" || exit /b 1
echo [2/4] Schema (database\schema_v5.sql) ...
"%PSQL%" -w -d %DB% -q -v ON_ERROR_STOP=1 -f "database\schema_v5.sql" >nul || (echo  *** schema failed & exit /b 1)
echo [3/4] Migrations in order ...
for %%f in ("database\migrations\*.sql") do (
  "%PSQL%" -w -d %DB% -q -v ON_ERROR_STOP=1 -f "%%~ff" >nul || (echo  *** MIGRATION FAILED: %%~nxf & exit /b 1)
  echo      %%~nxf
)
echo [4/4] Dev seed (database\seed\dev_seed.sql) ...
"%PSQL%" -w -d %DB% -q -v ON_ERROR_STOP=1 -f "database\seed\dev_seed.sql" >nul || (echo  *** seed failed & exit /b 1)
echo.
echo  Done: %DB% is ready. Accounts: ..\dock\docs\handoff\accounts.md (password Rahma-Dev-2026).
echo  To run the app on it, set DBNAME=%DB% in start-native.bat (or point ConnectionStrings__Default at it).
endlocal
