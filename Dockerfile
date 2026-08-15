# syntax=docker/dockerfile:1
#
# The hosted build of ING Listing Engine, for a Linux box behind a reverse proxy.
#
# This image is the HOSTED configuration and nothing else. The desktop build is a
# net10.0-windows app with a tray icon, a single-instance mutex and a fixed port that the eBay
# OAuth relay redirects to; none of that survives a container, and all of it is compiled out by
# the HOSTED constant. See the header of "ING eBay AutoLister.csproj" for how the two builds
# split, and HOSTING.md for how to run what comes out of here.
#
#   docker build -t ing-listing-engine .
#
# What this image will NOT do without configuration: start. A hosted build with no
# CREDENTIALS_ENCRYPTION_KEY throws on the way up, on purpose — it stores other people's eBay
# refresh tokens, and failing while somebody is reading the logs is better than the alternative.
# HOSTING.md lists every variable.


# ── Build ─────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project file alone first, so the restore layer is cached against dependency changes rather
# than against every edit to Program.cs.
#
# -p:Hosted=true on the RESTORE and not only the publish is load-bearing. The property is what
# selects the target framework (net10.0 rather than net10.0-windows), so a restore without it
# writes an assets file for the desktop framework and the --no-restore publish below then fails
# on a project.assets.json that does not contain the target it is being asked to build.
COPY ["ING eBay AutoLister/ING eBay AutoLister.csproj", "ING eBay AutoLister/"]
RUN dotnet restore "ING eBay AutoLister/ING eBay AutoLister.csproj" \
        -r linux-x64 -p:Hosted=true

COPY ["ING eBay AutoLister/", "ING eBay AutoLister/"]

# Framework-dependent against the aspnet runtime image below, rather than self-contained: the
# runtime is already in that image, and shipping a second copy inside the app folder only makes
# the image bigger and the .NET security updates something this Dockerfile has to chase.
RUN dotnet publish "ING eBay AutoLister/ING eBay AutoLister.csproj" \
        -c Release -r linux-x64 --self-contained false --no-restore \
        -p:Hosted=true -o /app/publish


# ── Runtime ───────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is here for one reason: the HEALTHCHECK at the bottom. The aspnet image ships neither curl
# nor wget, and a HEALTHCHECK that cannot run is a container that reports unhealthy forever.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# ASPNETCORE_URLS, because the hosted build binds whatever it is told to and nothing else — the
# port defence in the desktop build is compiled out. Bind all interfaces INSIDE the container;
# what stops the world reaching it is publishing the port to 127.0.0.1 only, which is the proxy's
# job and is spelled out in HOSTING.md.
#
# HOME and XDG_DATA_HOME are how the database path is set. The app keeps everything under
# GetFolderPath(LocalApplicationData)/"ING AutoLister", which .NET resolves on Linux to
# $XDG_DATA_HOME (falling back to $HOME/.local/share) — so with these two, the SQLite database
# lands at "/data/ING AutoLister/App_Data/ing_listing_engine.db" and the session key ring beside
# it. Both are set: XDG_DATA_HOME does the work, HOME makes the fallback land somewhere writable
# instead of on whatever the passwd entry says if that variable is ever cleared.
ENV ASPNETCORE_URLS=http://+:8080 \
    HOME=/data \
    XDG_DATA_HOME=/data \
    DOTNET_EnableDiagnostics=0

WORKDIR /app
COPY --from=build /app/publish .

# Non-root. APP_UID (1654) is the unprivileged account the .NET images already create; the
# fallback keeps this working on a base image that ever stops defining it.
#
# /data is created and handed to that account here so a NAMED VOLUME inherits the ownership when
# Docker initialises it from the image. A BIND MOUNT does not — the host directory's own
# ownership wins, and the app then cannot write its database. HOSTING.md says to chown the host
# directory to 1654 in that case.
RUN mkdir -p "/data/ING AutoLister/App_Data" \
 && chown -R ${APP_UID:-1654}:${APP_UID:-1654} /data

USER ${APP_UID:-1654}

EXPOSE 8080

# /health is one of the three endpoints on the anonymous allow-list (with sign-in and sign-up);
# every other endpoint in the app answers 401 without a session, so nothing else here is usable
# as a health probe. It reports that the process is answering and says nothing about secrets.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1

# Through the shared runtime rather than the apphost, so the image does not depend on the
# executable bit surviving whatever filesystem the publish output travelled across.
ENTRYPOINT ["dotnet", "AutoListerB1.dll"]
