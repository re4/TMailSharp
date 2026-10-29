FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/TMailSharp.Web/TMailSharp.Web.csproj src/TMailSharp.Web/
COPY src/TMailSharp.Web/packages.lock.json src/TMailSharp.Web/
RUN dotnet restore src/TMailSharp.Web/TMailSharp.Web.csproj --locked-mode
COPY src/TMailSharp.Web/ src/TMailSharp.Web/
RUN dotnet publish src/TMailSharp.Web/TMailSharp.Web.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/keys /app/identity && chown $APP_UID:$APP_UID /app/keys /app/identity
ENV ASPNETCORE_HTTP_PORTS=8080 Smtp__Host=0.0.0.0 Smtp__Port=2525
EXPOSE 8080 2525
USER $APP_UID
ENTRYPOINT ["dotnet", "TMailSharp.Web.dll"]
