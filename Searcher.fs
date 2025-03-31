[<AutoOpen>]
module Searcher
    open Config
    open System
    open System.IO
    open System.IO.Compression

    let findWordsAny(bytes: byte []) =
        let s = System.Text.Encoding.ASCII.GetString(bytes)
        config.keywords |> Seq.exists s.Contains
    
    let findWordsAll(bytes: byte []) =
        let s = System.Text.Encoding.ASCII.GetString(bytes)
        config.keywords |> Seq.forall s.Contains

    let tryGetZip(bytes: byte []) =
        [1..bytes.Length / 512 - 1]
            |> List.tryPick (fun k ->
                let zSpan = new Span<byte>(bytes)
                let zipStream = new MemoryStream(zSpan.Slice(0, k * 512).ToArray())
                try
                    let zip = new ZipArchive(zipStream, ZipArchiveMode.Read)
                    Some zip
                with _ -> 
                    zipStream.Dispose()
                    None
        )