module Queil.Gitlab.Jwt2Pat.App

open System
open System.IdentityModel.Tokens.Jwt
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Giraffe
open FsHttp
open FsHttp.DslCE
open Microsoft.Extensions.Options
open Microsoft.IdentityModel.Tokens

[<CLIMutable>]
type GitlabOptions =
  { Hostname: string
    ApiKey: string
    SudoUserLogin: string
    TokenConfig: TokenConfig }

and [<CLIMutable>] TokenConfig =
  { Scopes: string seq
    RevokeSeconds: int }

[<CLIMutable>]
type JwtOptions =
  { Authority: string
    Issuer: string
    Validate: Validate
    Debug: bool }

and [<CLIMutable>] Validate = { Audience: bool }

type RevocationQueue(logger: ILogger<RevocationQueue>, opts: IOptions<GitlabOptions>) =
  let channel = Channel.CreateUnbounded<Task>()
  let opts = opts.Value
  with
    member x.RegisterAsync(tokenId: int, userId: int) : ValueTask =
      channel.Writer.WriteAsync(
        task {
          do! Task.Delay(TimeSpan.FromSeconds(opts.TokenConfig.RevokeSeconds))

          try
            http {
              DELETE $"{opts.Hostname}/api/v4/users/{userId}/impersonation_tokens/{tokenId}"
              header "PRIVATE-TOKEN" opts.ApiKey
              query [ "sudo", opts.SudoUserLogin ]
            }
            |> Response.assert2xx
            |> ignore
            logger.LogInformation("Successfully revoked token {TokenId} for user {UserId}", tokenId, userId)
          with
          | exn -> logger.LogError(exn, "Token revocation failed")
        }
      )

    member _.Complete() = channel.Writer.Complete()
    member _.WaitToReadAsync(token) = channel.Reader.WaitToReadAsync(token)
    member _.ReadAsync() = channel.Reader.ReadAsync()

type RevocationService(lifetime: IHostApplicationLifetime, queue: RevocationQueue) =

  let mutable revokeLoopTask = Task.CompletedTask

  interface IHostedService with
    member x.StartAsync(_: CancellationToken) : Task =
      lifetime.ApplicationStopping.Register(fun () ->
        queue.Complete()
      )
      |> ignore

      revokeLoopTask <-
        task {
          try
            while not <| lifetime.ApplicationStopping.IsCancellationRequested do
              let! canRead = queue.WaitToReadAsync(lifetime.ApplicationStopping)

              if canRead then
                let! revokeTask = queue.ReadAsync()
                do! revokeTask
          with
          | :? OperationCanceledException ->
            ()
        }
      Task.CompletedTask

    member x.StopAsync(_: CancellationToken) : Task = task { do! revokeLoopTask }

let tokenHandler: HttpHandler =
  fun (_: HttpFunc) (ctx: HttpContext) ->

    let opts =
      ctx
        .RequestServices
        .GetRequiredService<IOptions<GitlabOptions>>()
        .Value

    task {
      let! jwtString = ctx.GetTokenAsync("access_token")

      let jwt =
        JwtSecurityTokenHandler().ReadJwtToken(jwtString)

      let userId = jwt.Payload.["user_id"] |> string |> int

      let result =
        http {
          POST $"{opts.Hostname}/api/v4/users/{userId}/impersonation_tokens"
          header "PRIVATE-TOKEN" opts.ApiKey
          query [ "sudo", opts.SudoUserLogin ]

          formUrlEncoded [ "expires_at", DateTime.UtcNow.AddDays(1).ToString("o")
                           "name", jwt.Subject
                           for s in opts.TokenConfig.Scopes do
                             "scopes[]", s ]
        }
        |> Response.assert2xx
        |> Response.toStream

      let! tokenResponse = JsonSerializer.DeserializeAsync<{| token: string; id: int |}>(result)

      do!
        ctx
          .GetService<RevocationQueue>()
          .RegisterAsync(tokenResponse.id, userId)

      return! ctx.WriteStringAsync tokenResponse.token
    }

let webApp =
  choose [ GET
           >=> choose [ route "/health" >=> Successful.OK "Healthy"
                        route "/token"
                        >=> requiresAuthentication (RequestErrors.UNAUTHORIZED "Bearer" "gitlab-pat" "")
                        >=> tokenHandler ]
           setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex: Exception) (logger: ILogger) =
  logger.LogError(ex, "An unhandled exception has occurred while executing the request.")

  clearResponse
  >=> setStatusCode 500
  >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let builder = WebApplication.CreateBuilder()
let services = builder.Services

let jwtOptions =
  builder
    .Configuration
    .GetSection("jwt")
    .Get<JwtOptions>()

services
  .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
  .AddJwtBearer(fun opts ->
    opts.BackchannelHttpHandler <- new HttpClientHandler()
    opts.Authority <- jwtOptions.Authority
    opts.SaveToken <- true

    if jwtOptions.Debug then
      opts.Events <-
        JwtBearerEvents(
          OnMessageReceived =
            fun x ->
              task {
                x
                  .HttpContext
                  .GetService<ILogger<JwtOptions>>()
                  .LogDebug("Auth header: {Token}", x.Request.Headers.Authorization)
              }
        )

    opts.TokenValidationParameters <-
      TokenValidationParameters(
        ValidateIssuer = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidateAudience = jwtOptions.Validate.Audience,
        ValidIssuer = jwtOptions.Issuer
      ))
|> ignore

services.Configure<GitlabOptions>(builder.Configuration.GetSection("gitlab"))
|> ignore

services.AddSingleton<RevocationQueue>() |> ignore

services.AddHostedService<RevocationService>()
|> ignore

services.AddCors() |> ignore
services.AddGiraffe() |> ignore

builder.Logging.AddConsole().AddDebug() |> ignore

let app = builder.Build()

if app.Environment.IsDevelopment() then
  app.UseDeveloperExceptionPage() |> ignore

app
  .UseGiraffeErrorHandler(errorHandler)
  .UseHttpsRedirection()
  .UseAuthentication()
  .UseGiraffe(webApp)

app.Run()
