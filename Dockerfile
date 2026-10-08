FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/B1Agent.Core/B1Agent.Core.csproj src/B1Agent.Core/
COPY src/B1Agent.Api/B1Agent.Api.csproj src/B1Agent.Api/
RUN dotnet restore src/B1Agent.Api/B1Agent.Api.csproj
COPY src/ src/
RUN dotnet publish src/B1Agent.Api/B1Agent.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "B1Agent.Api.dll"]
