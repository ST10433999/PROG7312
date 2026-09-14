# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.sln ./
COPY src/SmartX.Shared/SmartX.Shared.csproj src/SmartX.Shared/
COPY src/SmartX.Client/SmartX.Client.csproj src/SmartX.Client/
RUN dotnet restore src/SmartX.Client/SmartX.Client.csproj
COPY src/SmartX.Shared/ src/SmartX.Shared/
COPY src/SmartX.Client/ src/SmartX.Client/
RUN dotnet publish src/SmartX.Client/SmartX.Client.csproj -c Release -o /app/publish --no-restore

# ---- runtime: static files served by nginx ----------------------------------
FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
# Point the WASM app at the API container (overridable at runtime via API_BASE_URL)
COPY docker/client-entrypoint.sh /docker-entrypoint.d/40-smartx-config.sh
RUN chmod +x /docker-entrypoint.d/40-smartx-config.sh
EXPOSE 80
