FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN useradd --system --uid 10001 --home /app natter \
    && mkdir -p /config /state \
    && chown -R natter:natter /app /config /state
COPY --from=build /out .
USER natter
ENV NATTER_CONFIG_DIR=/config NATTER_STATE_DIR=/state
EXPOSE 8080
ENTRYPOINT ["dotnet", "natter.dll"]
CMD ["serve"]
