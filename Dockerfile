FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/IndexCreator/ src/IndexCreator/
RUN dotnet restore src/IndexCreator/IndexCreator.csproj --locked-mode
RUN dotnet publish src/IndexCreator/IndexCreator.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /tools/IndexCreator
COPY --from=build /app/ ./
COPY LICENSE NOTICE ThirdPartyNotices.txt ./
ENTRYPOINT ["dotnet", "VehicleVision.PleasanterTools.IndexCreator.dll"]
