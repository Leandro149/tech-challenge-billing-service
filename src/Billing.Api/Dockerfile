FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Billing.Api.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish --no-restore -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "Billing.Api.dll"]
