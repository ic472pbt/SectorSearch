module Init
    open Doc
    open Zip
    open System
    let cpuScanner = CPU.CPUStack(config.block_size, config.keywords, config.match_all)
    let are = new Threading.AutoResetEvent(false)    
    let zipSearchingAgent = Zip.zipSearchingAgentFabric(cpuScanner, are)
    let docSearchingAgent = Doc.docSearchingAgentFabric(cpuScanner)
    
    let filterZips = MailboxProcessor<byte[] * byte[] * int64>.Start(fun inbox ->
        let rec loop() = async {
            let! res, buffer, position = inbox.Receive()
            let zipCandidates = 
                res 
                    |> Array.mapi (fun i v -> if v = 100uy then Some i else None)
                    |> Array.choose id
            if zipCandidates.Length > 0 then
                let! zips = scanForLocalFileHeader (buffer, position, zipCandidates)
                zips
                    |> Array.iter (fun L -> 
                        L |> List.iter (Some >> zipSearchingAgent.Post)
                       )
            return! loop()
        }
        loop()
    )
    let filterDocs = MailboxProcessor<byte[] * byte[] * int64>.Start(fun inbox ->
        let rec loop() = async {
            let! res, buffer, position = inbox.Receive()
            let docCandidates = 
                res 
                    |> Array.mapi (fun i v -> if v = 200uy then Some i else None)
                    |> Array.choose id
            if docCandidates.Length > 0 then
                let! docs = scanDocs(buffer, position, docCandidates)
                docs
                    |> Array.iter (fun L -> 
                        L |> List.tryHead |> Option.iter (Some >> docSearchingAgent.Post)
                    )
            return! loop()
        }
        loop()
    )