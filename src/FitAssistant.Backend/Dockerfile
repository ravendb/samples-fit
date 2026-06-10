FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Packages.props", "./"]
COPY ["src/FitAssistant.Backend/FitAssistant.Backend.csproj", "src/FitAssistant.Backend/"]
COPY ["src/FitAssistant.ServiceDefaults/FitAssistant.ServiceDefaults.csproj", "src/FitAssistant.ServiceDefaults/"]
RUN dotnet restore "src/FitAssistant.Backend/FitAssistant.Backend.csproj"

COPY src/FitAssistant.Backend/ src/FitAssistant.Backend/
COPY src/FitAssistant.ServiceDefaults/ src/FitAssistant.ServiceDefaults/
WORKDIR /src/src/FitAssistant.Backend
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "FitAssistant.Backend.dll"]
