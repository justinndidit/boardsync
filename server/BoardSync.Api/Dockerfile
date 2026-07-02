FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BoardSync.Api.csproj ./
RUN dotnet restore BoardSync.Api.csproj

COPY . ./
RUN dotnet publish BoardSync.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S appgroup && adduser -S appuser -G appgroup

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER appuser
ENTRYPOINT ["dotnet", "BoardSync.Api.dll"]