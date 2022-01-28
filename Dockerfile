FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine as build

WORKDIR /build

COPY ./src/*.fsproj ./
RUN dotnet restore -r linux-musl-x64

COPY ./src ./
RUN dotnet publish -r linux-musl-x64 -c Release --no-restore --self-contained -o ./pkg

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine

ARG APP_DIR=/app
WORKDIR $APP_DIR

ARG USER=app

RUN addgroup -g 10001 -S $USER && \
    adduser -u 10001 -S $USER -G $USER && \
    chown $USER:$USER $APP_DIR && \
    chmod 755 $APP_DIR
ENV HOME /home/$USER

COPY --from=build /build/pkg ./

ENTRYPOINT ["/app/Queil.Gitlab.Jwt2Pat"]
