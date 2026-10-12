FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./

# Restore layer: every project in the Alpha.Api reference graph (keep in sync with AlphaBackend.slnx).
COPY src/Alpha.Domain/Alpha.Domain.csproj src/Alpha.Domain/
COPY src/Alpha.Infrastructure/Alpha.Infrastructure.csproj src/Alpha.Infrastructure/
COPY src/Alpha.Api/Alpha.Api.csproj src/Alpha.Api/
COPY src/Modules/Auditing/Alpha.Modules.Auditing/Alpha.Modules.Auditing.csproj src/Modules/Auditing/Alpha.Modules.Auditing/
COPY src/Modules/Billing/Alpha.Modules.Billing/Alpha.Modules.Billing.csproj src/Modules/Billing/Alpha.Modules.Billing/
COPY src/Modules/Billing/Alpha.Modules.Billing.Contracts/Alpha.Modules.Billing.Contracts.csproj src/Modules/Billing/Alpha.Modules.Billing.Contracts/
COPY src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess/Alpha.Modules.IdentityAccess.csproj src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess/
COPY src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess.Contracts/Alpha.Modules.IdentityAccess.Contracts.csproj src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess.Contracts/
COPY src/Modules/Organizations/Alpha.Modules.Organizations/Alpha.Modules.Organizations.csproj src/Modules/Organizations/Alpha.Modules.Organizations/
COPY src/Modules/ReferenceData/Alpha.Modules.ReferenceData/Alpha.Modules.ReferenceData.csproj src/Modules/ReferenceData/Alpha.Modules.ReferenceData/
COPY src/Modules/Reporting/Alpha.Modules.Reporting/Alpha.Modules.Reporting.csproj src/Modules/Reporting/Alpha.Modules.Reporting/
COPY src/Modules/Reporting/Alpha.Modules.Reporting.Contracts/Alpha.Modules.Reporting.Contracts.csproj src/Modules/Reporting/Alpha.Modules.Reporting.Contracts/
COPY src/Modules/Workforce/Alpha.Modules.Workforce/Alpha.Modules.Workforce.csproj src/Modules/Workforce/Alpha.Modules.Workforce/

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

# Keep the official Employer Interface 006 schemas in a deterministic runtime path.
# The validator resolves them from /app/Specifications/EmployerInterface/006.
COPY --from=build /src/docs/specifications/employer-interface/006/*.xsd.xml /app/Specifications/EmployerInterface/006/
COPY --from=build ["/src/docs/specifications/employer-interface/006/Employer interface V 6.xlsx", "/app/Specifications/EmployerInterface/006/"]

EXPOSE 10000

ENTRYPOINT ["dotnet", "Alpha.Api.dll"]
