# build stage: restore + publish the API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Meridian.sln ./
COPY Meridian.Api/Meridian.Api.csproj Meridian.Api/
COPY Meridian.Domain/Meridian.Domain.csproj Meridian.Domain/
RUN dotnet restore Meridian.Api/Meridian.Api.csproj
COPY Meridian.Api/ Meridian.Api/
COPY Meridian.Domain/ Meridian.Domain/
RUN dotnet publish Meridian.Api/Meridian.Api.csproj -c Release -o /app --no-restore

# runtime stage: small image, listens on 8080 (default for .NET 8 images)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Meridian.Api.dll"]
