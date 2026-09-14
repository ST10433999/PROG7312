# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.sln ./
COPY src/SmartX.Shared/SmartX.Shared.csproj src/SmartX.Shared/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj
COPY src/SmartX.Shared/ src/SmartX.Shared/
COPY src/SmartX.Api/ src/SmartX.Api/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    Attachments__Root=/data/attachments \
    Cors__AllowedOrigins__0=http://localhost:8081 \
    Cors__AllowedOrigins__1=http://localhost:5100
VOLUME ["/data/attachments"]
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD wget -qO- http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
