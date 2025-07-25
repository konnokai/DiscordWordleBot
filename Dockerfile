#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["DiscordWordleBot/DiscordWordleBot.csproj", "DiscordWordleBot/"]
RUN dotnet restore "DiscordWordleBot/DiscordWordleBot.csproj"
COPY . .
WORKDIR "/src/DiscordWordleBot"
RUN dotnet build "DiscordWordleBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DiscordWordleBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
COPY --from=publish /app/publish .

ENV TZ="Asia/Taipei"

STOPSIGNAL SIGQUIT

ENTRYPOINT ["dotnet", "DiscordWordleBot.dll"]