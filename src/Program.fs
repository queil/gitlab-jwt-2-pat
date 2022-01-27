module Queil.Gitlab.Jwt2Pat.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe

// ---------------------------------
// Models
// ---------------------------------

type Message =
    {
        Text : string
    }

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open Giraffe.ViewEngine

    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "src" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/main.css" ]
            ]
            body [] content
        ]

    let partial () =
        h1 [] [ encodedText "Queil.Gitlab.Jwt2Pat" ]

    let index (model : Message) =
        [
            partial()
            p [] [ encodedText model.Text ]
        ] |> layout

// ---------------------------------
// Web app
// ---------------------------------

let indexHandler (name : string) =
    let greetings = sprintf "Hello %s, from Giraffe!" name
    let model     = { Text = greetings }
    let view      = Views.index model
    htmlView view

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=> indexHandler "world"
                routef "/hello/%s" indexHandler
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

let configureCors (builder : CorsPolicyBuilder) =
    builder
        .WithOrigins(
            "http://localhost:5000",
            "https://localhost:5001")
       .AllowAnyMethod()
       .AllowAnyHeader()
       |> ignore


let contentRoot = Directory.GetCurrentDirectory()
let webRoot     = Path.Combine(contentRoot, "WebRoot")
let builder = WebApplication.CreateBuilder(WebApplicationOptions(
                ContentRootPath = Directory.GetCurrentDirectory(),
                EnvironmentName = Environments.Staging,
                WebRootPath = webRoot
))
let services = builder.Services
services.AddAuthentication() |> ignore
services.AddCors()    |> ignore
services.AddGiraffe() |> ignore
builder.Logging.AddConsole().AddDebug() |> ignore

let app = builder.Build()

match app.Environment.IsDevelopment() with
| true  ->
    app.UseDeveloperExceptionPage() |> ignore
| false ->
    app.UseGiraffeErrorHandler(errorHandler)
       .UseHttpsRedirection() 
       .UseCors(configureCors)
       .UseStaticFiles()
       .UseAuthentication()
       .UseGiraffe(webApp)
app.Run()
