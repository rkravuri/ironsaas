# syntax=docker/dockerfile:1
# Build from the shell directory: docker build -t foundation/iron-shell:dev shell/
# One build serves every tenant hostname (ADR-0020): nothing in it is tenant-specific.

FROM node:26-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

# NGINX's own unprivileged image: nginx runs as the user nginx and listens above 1024, as nginx.conf does on 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html
# The release's version (ADR-0052), given by the release workflow; the shell shows the unit's, from /me/context.
ARG IRON_VERSION=development
LABEL org.opencontainers.image.version=$IRON_VERSION
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
