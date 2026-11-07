#!/bin/sh
set -eu
# Railway provides PORT; local Docker defaults to 8080.
export ASPNETCORE_URLS="http://+:${PORT:-8080}"
exec dotnet /app/AlRahma.Api.dll
