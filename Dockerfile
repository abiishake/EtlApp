# ---- build (Debian-based SDK; only used inside this throw-away stage) ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY EtlApp.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out && ls /out

# ---- runtime (small Alpine image) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=0
COPY --from=build /out .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "EtlApp.dll"]
