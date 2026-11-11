# ---- build (SDK image; only used inside this throw-away stage) ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY EtlApp.csproj .
RUN dotnet restore EtlApp.csproj
COPY . .
RUN dotnet publish EtlApp.csproj -c Release -o /out

# ---- runtime (ASP.NET Core runtime, Debian based) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=0
ENV DATA_DIR=/data
COPY --from=build /out .
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
# the runtime image has no curl/wget: the app checks its own /healthz
HEALTHCHECK --interval=60s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "EtlApp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "EtlApp.dll"]
