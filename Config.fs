module Config 
    open FSharp.Configuration
    type Config = YamlConfig<"config.yaml">
    let config = Config()
