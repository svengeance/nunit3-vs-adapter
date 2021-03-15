FROM mcr.microsoft.com/dotnet/sdk:5.0-windowsservercore-ltsc2019 AS base
WORKDIR /tests
COPY . .
ENTRYPOINT ["dotnet", "test"]
