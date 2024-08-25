# Use the official .NET SDK image as a build stage
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["SetsunaScarletStream.fsproj", "./"]
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish -c Release -o /app

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0
WORKDIR /app
COPY --from=build /app .

# Install ffmpeg and tzdata
RUN apt-get update && apt-get install -y ffmpeg tzdata && rm -rf /var/lib/apt/lists/*

# Set timezone to JST
ENV TZ=Asia/Tokyo
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# Set the entry point
ENTRYPOINT ["dotnet", "SetsunaScarletStream.dll"]