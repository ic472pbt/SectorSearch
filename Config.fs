[<AutoOpen>]
module Config 
    open System.IO
    open FSharp.Configuration
    type Config = YamlConfig<"config.yaml">
    let config = Config()
    ["config_secret.yaml"; "config.yaml"]
        |> List.find File.Exists
        |> config.Load
