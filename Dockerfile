FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Robust.Cdn/Robust.Cdn.csproj", "Robust.Cdn/"]
RUN dotnet restore "Robust.Cdn/Robust.Cdn.csproj"
COPY . .
WORKDIR "/src/Robust.Cdn"
RUN dotnet build "Robust.Cdn.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Robust.Cdn.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Robust.Cdn.dll"]
VOLUME /database
ENV CDN__DatabaseFileName=/database/content.db
VOLUME /builds
ENV CDN__VersionDiskPath=/builds
