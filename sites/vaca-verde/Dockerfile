# Vaca Verde public website: build the prerendered static site, serve it with Caddy.
# Build context is this directory (Railway root directory `sites/vaca-verde`).
FROM node:22-slim AS build
WORKDIR /site
COPY package.json package-lock.json ./
RUN npm ci
COPY . ./
# Railway passes the service variable as a build argument; the build fails without it.
ARG VITE_APP_URL
ENV VITE_APP_URL=$VITE_APP_URL
RUN npm run build

FROM caddy:2-alpine
WORKDIR /srv
COPY Caddyfile /etc/caddy/Caddyfile
COPY --from=build /site/dist ./dist
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
