# ---------- Build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project file first so Docker can cache the restore step
COPY AdmissionApi/AdmissionApi.csproj AdmissionApi/
RUN dotnet restore AdmissionApi/AdmissionApi.csproj

# Copy the rest of the source code and publish a release build
COPY AdmissionApi/ AdmissionApi/
RUN dotnet publish AdmissionApi/AdmissionApi.csproj -c Release -o /app/publish

# ---------- Runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The assignment requires the API to listen on port 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AdmissionApi.dll"]