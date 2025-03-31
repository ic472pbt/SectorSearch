[<AutoOpen>]
module Config 
    open FSharp.Configuration
    type Config = YamlConfig<"config.yaml">
    let config = Config()
    config.Load("config.yaml")
