# addon-forge ships precompiled binaries — this Dockerfile does not build the
# .NET code. Run build.ps1 / build.sh first to produce ./dist, then this just
# packages it. That keeps `docker build` (which the Supervisor runs on every
# install/update) fast and avoids needing the SDK image on the target host.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# GitCliClient shells out to `git` for clone/ls-remote — not present in the base image.
RUN apk add --no-cache git

WORKDIR /app
COPY dist/ .

ENV ASPNETCORE_URLS=http://+:8099 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

ENTRYPOINT ["dotnet", "AddonForge.Web.dll"]
