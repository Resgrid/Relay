FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -p:TargetFramework=net10.0
RUN dotnet publish "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -c Release -f net10.0 -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV RELAY_Mode=smtp
ENV RELAY_Smtp__Port=2525

EXPOSE 2525

ENTRYPOINT ["dotnet", "Resgrid.Audio.Relay.Console.dll"]
