FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/Alpha.Domain/Alpha.Domain.csproj src/Alpha.Domain/
COPY src/Alpha.Application/Alpha.Application.csproj src/Alpha.Application/
COPY src/Alpha.Infrastructure/Alpha.Infrastructure.csproj src/Alpha.Infrastructure/
COPY src/Alpha.Api/Alpha.Api.csproj src/Alpha.Api/

RUN dotnet restore src/Alpha.Api/Alpha.Api.csproj

COPY . .
RUN dotnet publish src/Alpha.Api/Alpha.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000}
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

EXPOSE 10000

ENTRYPOINT ["dotnet", "Alpha.Api.dll"]
