module Queil.Gitlab.Jwt2Pat.App

open System
open System.IdentityModel.Tokens.Jwt
open System.Net.Http
open System.Text.Json
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
type GitlabOptions = {
    Hostname: string
    ApiKey: string
    SudoUserLogin: string
    TokenConfig: TokenConfig
}
and [<CLIMutable>]
TokenConfig = {
    Scopes: string seq
    ExpiresIn: TimeSpan
}
[<CLIMutable>]
type JwtOptions = {
    Authority: string
    Issuer: string
    Validate: Validate
    Debug: bool
}
and [<CLIMutable>]
Validate = {
   Audience: bool
}

let tokenHandler : HttpHandler  =
   fun  (_ : HttpFunc) (ctx : HttpContext) ->

        let opts = ctx.RequestServices.GetRequiredService<IOptions<GitlabOptions>>().Value

        task {
            let! jwtString = ctx.GetTokenAsync("access_token")
            let jwt = JwtSecurityTokenHandler().ReadJwtToken(jwtString)
            let userId = jwt.Payload["user_id"]
            let result =
                http {
                   POST $"{opts.Hostname}/api/v4/users/{userId}/impersonation_tokens"
                   header "PRIVATE-TOKEN" opts.ApiKey
                   query [
                       "sudo", opts.SudoUserLogin
                   ]
                   formUrlEncoded [
                     "expires_at", DateTimeOffset.Now.Add(opts.TokenConfig.ExpiresIn).ToIsoString()
                     "name", jwt.Subject
                     for s in opts.TokenConfig.Scopes do
                       "scopes[]", s
                  ]
                }
                |> Response.assert2xx
                |> Response.toStream
            let! responseContent = JsonSerializer.DeserializeAsync<{|token:string|}>(result)
            return! ctx.WriteStringAsync responseContent.token
        }

let webApp =
    choose [
        GET >=>
            choose [
                route "/health" >=> Successful.OK "Healthy"
                route "/token" >=> requiresAuthentication (RequestErrors.UNAUTHORIZED "Bearer" "gitlab-pat" "") >=> tokenHandler
            ]
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let builder = WebApplication.CreateBuilder()
let services = builder.Services
let jwtOptions = builder.Configuration.GetSection("jwt").Get<JwtOptions>()

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(fun opts ->
            opts.BackchannelHttpHandler <- new HttpClientHandler()
            opts.Authority <- jwtOptions.Authority
            opts.SaveToken <- true
            if jwtOptions.Debug then
                opts.Events <- JwtBearerEvents(OnMessageReceived = fun x ->
                     task {
                        x.HttpContext.GetService<ILogger<JwtOptions>>().LogDebug("Auth header: {Token}", x.Request.Headers.Authorization)
                     })
            opts.TokenValidationParameters <- TokenValidationParameters(
                ValidateIssuer = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidateAudience = jwtOptions.Validate.Audience,
                ValidIssuer = jwtOptions.Issuer
            )
        ) |> ignore

services.Configure<GitlabOptions>(builder.Configuration.GetSection("gitlab")) |> ignore
services.AddCors()    |> ignore
services.AddGiraffe() |> ignore

builder.Logging.AddConsole().AddDebug() |> ignore

let app = builder.Build()

if app.Environment.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore

app.UseGiraffeErrorHandler(errorHandler)
   .UseHttpsRedirection() 
   .UseAuthentication()
   .UseGiraffe(webApp)

app.Run()
