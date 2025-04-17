open System
open System.IO
open Doc
open Zip
open IO

open FSharp.Control

type Msg =
    | MsgSector of (byte[] * int64)
    | MsgCompleteCluster of (byte[] * int64)
    | MsgIncompleteCluster of (byte[] * int64)
    | MsgMiss
let cts = new System.Threading.CancellationTokenSource()

// read windows-1251 encoding from doc
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)

let handleCancelKeyPress (args: ConsoleCancelEventArgs) =
    printfn "Cancellation requested..."
    IO.cts.Cancel()
    Zip.cts.Cancel()
    cts.Cancel()
    args.Cancel <- true

printfn "Press Ctrl+C to cancel the operation."
Console.CancelKeyPress.Add(handleCancelKeyPress)

printfn "keywords : %A" config.keywords
if not <| Directory.Exists (config.output_dir) then
    Directory.CreateDirectory(config.output_dir) |> ignore
if not <| Directory.Exists (Path.Combine(config.output_dir, "zip")) then
    Directory.CreateDirectory(Path.Combine(config.output_dir, "zip")) |> ignore

let degreeOfParallelizm = System.Environment.ProcessorCount

/// Do keyword search in block
let findWords = if config.match_all then findWordsAll else findWordsAny
let kwMatcher() = MailboxProcessor.Start(fun inbox ->
    let rec loop() = async {
        let! (buffer, offset) = inbox.Receive()
        let res = findWords buffer
        if res then
            let fileName = Path.Combine(config.output_dir, $"{offset}.txt")
            do! File.WriteAllBytesAsync(fileName, buffer) |> Async.AwaitTask
            printfn "found at offset: %i" offset
        return! loop()
    }
    loop()
)

let clusterSaver = MailboxProcessor<byte[] * int64>.Start(fun inbox -> 
    let rec loop() = async {
        let! (buffer, offset) = inbox.Receive()
        let fileName = Path.Combine(config.output_dir,"zip", $"{offset}.zip")
        do! File.WriteAllBytesAsync(fileName, buffer) |> Async.AwaitTask
        printfn "zip file saved at offset: %i" offset
        return! loop()
    }
    loop()
)

let zipMatcher = MailboxProcessor<Msg>.Start(fun inbox ->
    let rec loop(prevCluster: (byte[] * int64) option) = async {
        let! msg = inbox.Receive()
        match msg, prevCluster with
        | MsgMiss, Some _ ->
            printfn "zip file is too big, skip"
            return! loop None
        | MsgCompleteCluster (buffer, offset), None ->
            clusterSaver.Post (buffer, offset)
            let zip = tryGetZip buffer
            match zip with
            | Some zip ->
                for el in zip.Entries do
                    if el.Name.EndsWith(".xml") then
                        use f = el.Open()
                        let buffer = Array.zeroCreate<byte> <| (int)el.Length
                        f.Read(buffer, 0, buffer.Length) |> ignore
                        if findWords buffer then
                            let fileName = Path.Combine(config.output_dir, el.Name)                
                            do! File.WriteAllBytesAsync(fileName, buffer) |> Async.AwaitTask
                            printfn "found in zip at offset: %i" offset
                zip.Dispose()
            | None -> ()
            return! loop None
        | MsgCompleteCluster (buffer, offset), Some (bufferP, offsetP) ->
            let cluster = Array.zeroCreate<byte> (bufferP.Length + buffer.Length)
            System.Array.Copy(bufferP, 0, cluster, 0, bufferP.Length)
            System.Array.Copy(buffer, 0, cluster, bufferP.Length, buffer.Length)
            clusterSaver.Post (cluster, offsetP)
            let zip = tryGetZip cluster
            match zip with
            | Some zip -> 
                try
                    try
                        for el in zip.Entries do
                            if el.Name.EndsWith(".xml") then
                                use f = el.Open()
                                let buffer = Array.zeroCreate<byte> <| (int)el.Length
                                f.Read(buffer, 0, buffer.Length) |> ignore
                                if findWords buffer then
                                    let fileName = Path.Combine(config.output_dir, el.Name)                
                                    do! File.WriteAllBytesAsync(fileName, buffer) |> Async.AwaitTask
                                    printfn "found in zip at offset: %i" offset
                    with ex -> printfn "%s" ex.Message
                finally
                    zip.Dispose()
            | None -> ()
            return! loop None
        | MsgIncompleteCluster C, None -> return! loop (Some C)
        | _, _ -> return! loop None
            
    }
    loop(None)
)


/// Select sectors containing keywords
let filterFound = MailboxProcessor<byte[] * byte[] * int64 * string option>.Start(fun inbox ->
    let wordsLimit = (byte)config.min_words
    let rec loop() = async {
        let! (res, buffer, clusterPosition, info) = inbox.Receive()
        res |> Array.iteri (fun i wordsCount ->
            if wordsCount >= wordsLimit && wordsCount < 100uy then
                let sectorPosition = i * 512
                storeSector.Post <| StoreSector(buffer, sectorPosition, clusterPosition, wordsCount, info)
        )
        return! loop()
    }
    loop()
)

let cpuScanner = CPU.CPUStack(config.block_size, config.keywords, config.match_all)
let scanner: IStack.IStack = 
    if config.use_gpu then
        GPU.GPUStack(config.block_size, config.keywords, config.match_all)
    else
        cpuScanner

let docSearchingAgent = Doc.docSearchingAgentFabric(cpuScanner)

let hddImage = File.Open(config.input_file, FileMode.Open, FileAccess.Read, FileShare.Read)    
let hddImageSize = 
    hddImage.Length
printfn "file size: %i" hddImageSize
try
    // Scip txt files scanning. Search in zip files only
    if not config.skip_hdd_scan then
        let readBlockSize = config.block_size * 512
        printfn "block size: %i" readBlockSize

        readStream hddImageSize readBlockSize hddImage
            |> AsyncSeq.iterAsync (fun (buffer, position) -> async{
                    let! res = scanner.Scan(buffer, position)
                    filterFound.Post (res, buffer, position, None)
                    let zipCandidates = 
                        res 
                            |> Array.mapi (fun i v -> if v = 100uy then Some i else None)
                            |> Array.choose id
                    let docCandidates = 
                        res 
                            |> Array.mapi (fun i v -> if v = 200uy then Some i else None)
                            |> Array.choose id
                    if zipCandidates.Length > 0 then
                        do! scanForLocalFileHeader (buffer, position, zipCandidates)
                    if docCandidates.Length > 0 then
                        let! docs = scanDocs(buffer, position, docCandidates)
                        docs
                            |> Array.iter (fun L -> 
                                L |> List.tryHead |> Option.iter (Some >> docSearchingAgent.Post)
                            )
                })
            |> Async.RunSynchronously

    storeLocalHeaderOffset.PostAndReply CompleteStoreOffset

    printfn "Searching in zip files..."
    let zipBlockSize = 6*512
    //let suspects =
    identifyLFHPositions hddImage
        |> Seq.iter (fun (position, fileName, uncompressedSize, decompressedStream) ->
            try
                if fileName.EndsWith ".zip" || fileName.EndsWith ".gz" then
                    let targetFn = Path.Combine(config.output_dir, "zip", position.ToString() + " " + Path.GetFileName fileName)
                    let file = File.Open(targetFn, FileMode.Create, FileAccess.Write)
                    try
                        try
                            decompressedStream.CopyTo(file, 1024)
                        with ex ->
                            printfn "Error reading zip file: %s \r\n %s" fileName ex.Message
                    finally
                         file.Flush()
                         let fileSize = file.Length
                         file.Close()              
                         if fileSize = 0 then File.Delete targetFn                            
                else
                    try
                        readStream uncompressedSize zipBlockSize decompressedStream
                            |> AsyncSeq.iterAsync (fun (buffer, clusterPosition) -> async{
                                    let! res = cpuScanner.Scan(buffer, clusterPosition)
                                    let info = Some (sprintf "%s %i" fileName clusterPosition)
                                    filterFound.Post (res, buffer, clusterPosition, info)
                                })
                            |> Async.RunSynchronously
                    with ex ->
                        printfn "Error reading zip file: %s \r\n %s" fileName ex.Message
            finally
                decompressedStream.Close()
            )        

    // wait for the writer to finish
    storeSector.PostAndReply Complete
finally
    docSearchingAgent.Post None
    hddImage.Close()
