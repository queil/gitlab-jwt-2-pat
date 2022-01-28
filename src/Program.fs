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
    hostname: string
    apiKey: string
}

let tokenHandler : HttpHandler  =
   fun  (next : HttpFunc) (ctx : HttpContext) ->

        let opts = ctx.RequestServices.GetRequiredService<IOptions<GitlabOptions>>().Value

        task {
            let! jwtString = ctx.GetTokenAsync("Bearer")
            let jwt = JwtSecurityTokenHandler().ReadJwtToken(jwtString)
            let userId = jwt.Payload["user_id"]
            let expiresAt = jwt.ValidTo.ToIsoString()
            let name = jwt.Subject
            let result =
                http {
                   POST $"{opts.hostname}/api/v4/users/{userId}/impersonation_tokens"
                   header "PRIVATE-TOKEN" opts.apiKey
                   formUrlEncoded
                     [
                         "expires_at", expiresAt
                         "name", name
                     ]
                }
                |> Response.assertOk
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
let jwtIssuer = builder.Configuration.GetValue("jwt:issuer")

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(fun opts ->
            opts.BackchannelHttpHandler <- new HttpClientHandler()
            opts.Authority <- jwtIssuer
            opts.RequireHttpsMetadata <- false
            opts.MetadataAddress <- $"{jwtIssuer}/.well-known/openid-configuration"
            opts.TokenValidationParameters <- TokenValidationParameters(
                ValidateIssuer = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer
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
   .UseStaticFiles()
   .UseAuthentication()
   .UseGiraffe(webApp)

app.Run()
