[<AutoOpen>]
module Config 
    open System
    open System.IO
    open FSharp.Configuration
    type Config = YamlConfig<"config.yaml">
    let config = Config()
    ["config_secret.yaml"; "config.yaml"]
        |> List.find File.Exists
        |> config.Load
    let signatures =
        config.signatures |> Seq.map (fun L -> L |> Seq.map (fun h -> Byte.Parse(h.[2..3], Globalization.NumberStyles.HexNumber)) |> Seq.toArray) |> Seq.toList
    let keywords =
        [
            yield! config.keywords |> Seq.map (fun L -> Text.Encoding.ASCII.GetBytes(L)) |> Seq.toList            
            yield! config.keywords |> Seq.map (fun L -> Text.Encoding.Unicode.GetBytes(L)) |> Seq.toList
        ]