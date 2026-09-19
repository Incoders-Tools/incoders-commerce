# Repo-root build context (Railway's `build` object has no `dockerContext`
# property outside a multi-service `services[]` array — design.md "Deploy
# mechanism"). Every COPY below is repo-root-relative regardless of
# `railway.json`'s `build.dockerfilePath`.

# --- SPA build stage ---------------------------------------------------
# Built separately from the .NET stage and copied into Cloud.Api's wwwroot
# below (design.md "SPA delivery" — same-origin, one Railway service, no
# CORS). Restore npm deps first for layer caching.
FROM node:22-slim AS web-build
WORKDIR /web
COPY src/Commerce.Web/package.json src/Commerce.Web/package-lock.json ./
RUN npm ci
COPY src/Commerce.Web/ ./
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Same-origin SPA bundle served by Program.cs's UseDefaultFiles/
# UseStaticFiles/MapFallbackToFile("index.html") — never built or committed
# separately (see .gitignore's src/Commerce.Cloud.Api/wwwroot/ entry).
COPY --from=web-build /web/dist ./wwwroot

# Kestrel binds 0.0.0.0 and reads the injected PORT env var at runtime
# (Program.cs's explicit ConfigureKestrel/ListenAnyIP) — no hardcoded port
# here; Railway injects PORT at deploy time.

ENTRYPOINT ["dotnet", "Commerce.Cloud.Api.dll"]
