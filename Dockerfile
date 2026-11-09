ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${SDK_IMAGE} AS build
WORKDIR /source

ARG PROJECT_PATH=src/Financial.Api/Financial.Api.csproj
ARG APP_DLL=Financial.Api.dll
RUN test -n "$PROJECT_PATH" && test -n "$APP_DLL"
COPY . .
RUN dotnet restore "$PROJECT_PATH"
RUN dotnet publish "$PROJECT_PATH" --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
ARG APP_DLL=Financial.Api.dll
ENV ASPNETCORE_HTTP_PORTS=8080
ENV APP_DLL=${APP_DLL}
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
