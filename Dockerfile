# syntax=docker/dockerfile:1
# Single-origin production image (publish cp1): the API serves the built SPA from wwwroot,
# plus a self-contained EF migrations bundle for Railway's pre-deploy step.
#
# The build context is a PARENT directory holding BOTH repos — this file only references
# the context through the two ARGs below:
#   CI (deploy.yml):  checks out backend/ + frontend/ and builds with the defaults.
#   Local rehearsal:  from the folder containing both clones:
#     docker build -f Recipe_App_Back/Dockerfile \
#       --build-arg BACKEND_DIR=Recipe_App_Back --build-arg FRONTEND_DIR=Recipe_App_Front \
#       -t recipeapp .
ARG BACKEND_DIR=backend
ARG FRONTEND_DIR=frontend

FROM node:22-alpine AS frontend-build
ARG FRONTEND_DIR
WORKDIR /src
COPY ${FRONTEND_DIR}/package.json ${FRONTEND_DIR}/package-lock.json ./
# The lockfile is written by npm 11 on the dev machines; the image ships npm 10, whose
# ci mishandles npm-11 locks (nested platform optionals install as required →
# EBADPLATFORM). Same npm major as the lock author = deterministic installs.
RUN npm install -g npm@11 && npm ci
COPY ${FRONTEND_DIR}/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
ARG BACKEND_DIR
WORKDIR /src
COPY ${BACKEND_DIR}/ ./
RUN dotnet restore RecipeApp.slnx
RUN dotnet publish RecipeApp.API/RecipeApp.API.csproj -c Release -o /out/app --no-restore

# Self-contained migrations bundle (runs on the bare aspnet image with no SDK). Bundling
# never connects to a database, but the design-time factory (ApplicationDbContextFactory)
# reads appsettings.json from the cwd and insists on a connection string — hence the
# RecipeApp.API working directory and the throwaway value.
RUN dotnet tool install --global dotnet-ef --version "10.*"
ENV PATH="$PATH:/root/.dotnet/tools"
WORKDIR /src/RecipeApp.API
RUN ConnectionStrings__DefaultConnection="Host=localhost;Database=design;Username=design;Password=design" \
    dotnet ef migrations bundle \
      --project ../RecipeApp.Infrastructure --startup-project . \
      --configuration Release --self-contained -r linux-x64 -o /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=backend-build /out/app ./
COPY --from=backend-build /out/efbundle ./efbundle
COPY --from=frontend-build /src/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RecipeApp.API.dll"]
