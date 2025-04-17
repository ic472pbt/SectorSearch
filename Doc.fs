module Doc
    open System
    open System.IO
    open NPOI.HWPF
    open NPOI.HWPF.Extractor
    open Zip

    let DOC_SIGNATURE = 0xE11AB1A1E011CFD0UL      // little endian

    if not <| Directory.Exists (Path.Combine(config.output_dir, "doc")) then
        Directory.CreateDirectory(Path.Combine(config.output_dir, "doc")) |> ignore
    
    /// Detect doc signature in a sector
    let detectDoc (sector: ReadOnlyMemory<byte>, offset: int64) =
        use ms = new MemoryStream(sector.ToArray())
        use br = new BinaryReader(ms)
        let indexes =
            [0L .. int64(sector.Length - 9)]
                |> List.choose (fun i ->
                        ms.Seek(i, SeekOrigin.Begin) |> ignore
                        let signature = br.ReadUInt64()
                        if signature = DOC_SIGNATURE then
                            Some <| int64 i + offset
                        else None
                    )
        if indexes.Length > 0 then
            storeLocalHeaderOffset.Post(StoreDocOffset(indexes, offset))
        indexes

    /// Scan the cluster/block for doc signature
    let scanDocs(cluster: byte [], position: int64, candidates: int []) =
            candidates
                |> Array.map (fun i -> async {
                        let offset = i * 512
                        let sector = ReadOnlyMemory<byte>(cluster, offset, 512)
                        return detectDoc(sector, position + int64 offset)
                    })
                |> Async.Parallel

    let docSearchingAgentFabric(searcher: IStack.IStack) = MailboxProcessor<int64 option>.Start(fun inbox ->
        let hddImage = File.Open(config.input_file, FileMode.Open, FileAccess.Read, FileShare.Read)    
        
        let rec loop() = async {
            let! msg = inbox.Receive()
            match msg with
            | Some position -> 
                hddImage.Seek(position, SeekOrigin.Begin) |> ignore
                try
                    let buffer = Array.zeroCreate<byte> <| 1024 * 1024 * 20 // 20Mb
                    let! len = hddImage.ReadAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
                    use ms = new MemoryStream(buffer, 0, len)
                    let doc = HWPFDocument(ms)
                    let extractor = WordExtractor(doc)

                    let content = extractor.Text
                   // if searcher.SearchIn content > 0 then
                    File.WriteAllText(Path.Combine(config.output_dir, "doc", position.ToString() + ".txt"), content)
                    File.WriteAllBytes(Path.Combine(config.output_dir, "doc", position.ToString() + ".doc"), buffer)
                with ex -> 
                    printfn "Error reading document at position %d: %s" position ex.Message
            | _ -> hddImage.Close()
            return! loop()
        }
        loop()
    )