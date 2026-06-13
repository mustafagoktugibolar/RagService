FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Rag.Api/Rag.Api.csproj", "src/Rag.Api/"]
COPY ["src/Rag.Core/Rag.Core.csproj", "src/Rag.Core/"]
RUN dotnet restore "src/Rag.Api/Rag.Api.csproj"
COPY . .
WORKDIR "/src/src/Rag.Api"
RUN dotnet build "Rag.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Rag.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Rag.Api.dll"]
