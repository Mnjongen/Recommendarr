# syntax=docker/dockerfile:1
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -t recommendarr .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Set by the release workflow from the git tag (v1.2.3 -> 1.2.3).
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY Recommendarr.slnx ./
COPY src/Recommendarr.Core/Recommendarr.Core.csproj src/Recommendarr.Core/
COPY src/Recommendarr.Infrastructure/Recommendarr.Infrastructure.csproj src/Recommendarr.Infrastructure/
COPY src/Recommendarr.Web/Recommendarr.Web.csproj src/Recommendarr.Web/
RUN dotnet restore src/Recommendarr.Web/Recommendarr.Web.csproj -a $TARGETARCH

COPY src/ src/
# Publishing for the target RID copies the matching sqlite-vec native (vec0.so) into the output.
RUN dotnet publish src/Recommendarr.Web/Recommendarr.Web.csproj -a $TARGETARCH --no-restore -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Recommendarr__Storage__DataDirectory=/config

RUN mkdir -p /config && chown app:app /config
VOLUME /config
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "Recommendarr.Web.dll"]
