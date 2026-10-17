# Repo-root build context (Railway builds from the service's root directory,
# the repository root — design.md "Deploy mechanism"). Every COPY below is
# repo-root-relative.

# --- SPA build stage ---------------------------------------------------
# Built separately from the .NET stage and copied into Cloud.Api's wwwroot
# below (design.md "SPA delivery" — same-origin, one Railway service, no
# CORS). Restore npm deps first for layer caching.
FROM node:22-slim AS web-build
WORKDIR /web
COPY src/Commerce.Web/package.json src/Commerce.Web/package-lock.json ./
RUN npm ci
COPY src/Commerce.Web/ ./
# Site name shown in the browser tab (index.html's %VITE_SITE_NAME%). Railway
# passes the service variable of the same name as this build argument; the
# build fails without it instead of shipping a literal placeholder.
ARG VITE_SITE_NAME
RUN test -n "$VITE_SITE_NAME" || { echo "error: the VITE_SITE_NAME build argument is required (the web app's site name)." >&2; exit 1; }
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching: copy only the project files this image
# actually needs, keeping the graph limited to Cloud.Api's dependencies.
COPY src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj src/Commerce.Cloud.Api/
COPY src/Commerce.Domain/Commerce.Domain.csproj src/Commerce.Domain/
COPY src/Commerce.Application/Commerce.Application.csproj src/Commerce.Application/
COPY src/Commerce.BranchNode/Commerce.BranchNode.csproj src/Commerce.BranchNode/
RUN dotnet restore src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj

COPY src/Commerce.Cloud.Api/ src/Commerce.Cloud.Api/
COPY src/Commerce.Domain/ src/Commerce.Domain/
COPY src/Commerce.Application/ src/Commerce.Application/
COPY src/Commerce.BranchNode/ src/Commerce.BranchNode/

RUN dotnet publish src/Commerce.Cloud.Api/Commerce.Cloud.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Migration runner, executed by the Railway service's Pre-deploy Command (a
# dashboard setting per environment, deploy/railway-runbook.md section 5)
# before each new deployment serves traffic. It ships next to its migrations,
# its default directory (ADR-016).
COPY src/Commerce.Migrator/ src/Commerce.Migrator/
RUN dotnet publish src/Commerce.Migrator/Commerce.Migrator.csproj \
    -c Release \
    -o /app/migrator
COPY deploy/db/migrations/ /app/migrator/migrations/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Same-origin SPA bundle served by Program.cs's UseDefaultFiles/
# UseStaticFiles/MapFallbackToFile("index.html") — never built or committed
# separately (see .gitignore's src/Commerce.Cloud.Api/wwwroot/ entry).
COPY --from=web-build /web/dist ./wwwroot
COPY --from=build /app/migrator ./migrator

# Kestrel binds 0.0.0.0 and reads the injected PORT env var at runtime
# (Program.cs's explicit ConfigureKestrel/ListenAnyIP) — no hardcoded port
# here; Railway injects PORT at deploy time.

# CMD, not ENTRYPOINT: Railway's start and pre-deploy commands replace it, so
# the pre-deploy container runs the migrator instead of the API.
CMD ["dotnet", "Commerce.Cloud.Api.dll"]
