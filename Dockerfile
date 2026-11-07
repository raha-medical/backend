FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AlRahma.Api/AlRahma.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --chmod=755 deploy/start-api.sh /app/start-api.sh
RUN mkdir -p /app/App_Data /keys && chown -R app:app /app/App_Data /keys
ENV ASPNETCORE_ENVIRONMENT=Production
USER app
EXPOSE 8080
ENTRYPOINT ["/app/start-api.sh"]
