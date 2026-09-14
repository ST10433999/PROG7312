#!/bin/sh
# Rewrites wwwroot/appsettings.json so the WASM client calls the API at $API_BASE_URL.
: "${API_BASE_URL:=http://localhost:8080/}"
echo "{ \"ApiBaseUrl\": \"${API_BASE_URL}\" }" > /usr/share/nginx/html/appsettings.json
echo "Smart-X client configured for API at ${API_BASE_URL}"
