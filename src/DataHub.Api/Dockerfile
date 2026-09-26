# Build from the repository root: docker build -f src/DataHub.Api/Dockerfile .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f src/DataHub.Api/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish src/DataHub.Api/DataHub.Api.csproj -c Release -a $TARGETARCH -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DataHub.Api.dll"]
