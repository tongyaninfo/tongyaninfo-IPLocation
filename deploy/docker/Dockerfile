# 多阶段构建：适用于可访问 npm 与 NuGet 的 CI 或构建机。
# 目标机网络受限时，可先在本机构建产物，再使用 Dockerfile.prebuilt 打包。
# 构建上下文为仓库根：
#   docker build -f deploy/docker/Dockerfile -t iplocation-api:local .

FROM node:24-bookworm AS frontend
WORKDIR /src/frontend
# npm 源可通过构建参数覆盖：默认使用官方源；国内构建可传 --build-arg NPM_REGISTRY=https://registry.npmmirror.com
ARG NPM_REGISTRY=https://registry.npmjs.org
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/ ./backend/
COPY --from=frontend /src/frontend/dist ./frontend/dist
RUN dotnet restore backend/src/IpLocation.Api/IpLocation.Api.csproj
RUN dotnet publish backend/src/IpLocation.Api/IpLocation.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=backend /app/publish ./
COPY --from=frontend /src/frontend/dist ./frontend/dist
EXPOSE 5080
ENTRYPOINT ["dotnet", "IpLocation.Api.dll"]
