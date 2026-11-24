FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Moonlight.sln Directory.Build.props .editorconfig ./
COPY src/Moonlight.Api/Moonlight.Api.csproj src/Moonlight.Api/
COPY tests/Moonlight.Tests/Moonlight.Tests.csproj tests/Moonlight.Tests/
RUN dotnet restore src/Moonlight.Api/Moonlight.Api.csproj
COPY src/ src/
# Clinic identity (name, colours, logo, default features) read at startup.
COPY clinic.config.json clinic.config.demo.json clinic.config.schema.json ./
COPY branding/ branding/
RUN dotnet publish src/Moonlight.Api/Moonlight.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# tzdata: slot times are stored in UTC and shown in the clinic timezone (Africa/Cairo).
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Moonlight.Api.dll"]
