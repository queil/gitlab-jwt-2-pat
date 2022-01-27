FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine as build

WORKDIR /build

COPY ./src/*.fsproj ./
RUN dotnet restore -r linux-musl-x64

COPY ./src ./
RUN dotnet publish -r linux-musl-x64 -c Release --no-restore --self-contained -o ./pkg

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine

WORKDIR /app
COPY --from=build /build/pkg ./

ENTRYPOINT ["/app/Queil.Gitlab.Jwt2Pat"]
