FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/A18.Realtime.Core/A18.Realtime.Core.csproj src/A18.Realtime.Core/
COPY src/A18.YuantaSpark/A18.YuantaSpark.csproj src/A18.YuantaSpark/
COPY src/A18.Realtime.Api/A18.Realtime.Api.csproj src/A18.Realtime.Api/
COPY vendor/yuanta/ vendor/yuanta/
RUN dotnet restore src/A18.Realtime.Api/A18.Realtime.Api.csproj

COPY src/ src/
RUN dotnet publish src/A18.Realtime.Api/A18.Realtime.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv python3-pip && rm -rf /var/lib/apt/lists/*
COPY shioaji /opt/shioaji
RUN python3 -m venv /opt/shioaji/.venv && /opt/shioaji/.venv/bin/pip install --no-cache-dir "shioaji>=1.7.2"
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV TZ=Asia/Taipei
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "A18.Realtime.Api.dll"]
