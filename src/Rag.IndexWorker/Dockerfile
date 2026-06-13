FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Rag.Core/Rag.Core.csproj", "src/Rag.Core/"]
COPY ["src/Rag.IndexWorker/Rag.IndexWorker.csproj", "src/Rag.IndexWorker/"]
RUN dotnet restore "src/Rag.IndexWorker/Rag.IndexWorker.csproj"
COPY . .
WORKDIR "/src/src/Rag.IndexWorker"
RUN dotnet build "./Rag.IndexWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Rag.IndexWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Rag.IndexWorker.dll"]
