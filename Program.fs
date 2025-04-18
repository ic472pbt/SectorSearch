open System
open System.IO
open Log
open IO
open Zip
open Filter
open Init

open FSharp.Control
initLog()
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


let scanner: IStack.IStack = 
    if config.use_gpu then
        GPU.GPUStack(config.block_size, config.keywords, config.match_all)
    else
        cpuScanner


let hddImage = File.Open(config.input_file, FileMode.Open, FileAccess.Read, FileShare.Read)    
let hddImageSize = 
    hddImage.Length
printfn "file size: %i" hddImageSize
try
    // Scip txt files scanning. Search in zip files only
    if not config.skip_hdd_scan then
        let readBlockSize = config.block_size * 512
        printfn "block size: %i" readBlockSize

        readStream true hddImageSize readBlockSize hddImage
            |> AsyncSeq.iter (fun (buffer, position) -> 
                    let res = scanner.Scan(buffer, position)
                    filterFound.Post(res, buffer, position, None)
                    filterZips.Post(res, buffer, position)
                    filterDocs.Post(res, buffer, position)
                )
            |> Async.RunSynchronously

    storeLocalHeaderOffset.PostAndReply CompleteStoreOffset

    //printfn "Searching in zip files..."
    //let zipBlockSize = 6*512
    ////let suspects =
    //identifyLFHPositions hddImage
    //    |> Seq.iter (fun (position, fileName, uncompressedSize, decompressedStream) ->
    //        try
    //            if fileName.EndsWith ".zip" || fileName.EndsWith ".gz" then
    //                let targetFn = Path.Combine(config.output_dir, "zip", position.ToString() + " " + Path.GetFileName fileName)
    //                let file = File.Open(targetFn, FileMode.Create, FileAccess.Write)
    //                try
    //                    try
    //                        decompressedStream.CopyTo(file, 1024)
    //                    with ex ->
    //                        printfn "Error reading zip file: %s \r\n %s" fileName ex.Message
    //                finally
    //                     file.Flush()
    //                     let fileSize = file.Length
    //                     file.Close()              
    //                     if fileSize = 0 then File.Delete targetFn                            
    //            else
    //                try
    //                    readStream uncompressedSize zipBlockSize decompressedStream
    //                        |> AsyncSeq.iterAsync (fun (buffer, clusterPosition) -> async{
    //                                let! res = cpuScanner.Scan(buffer, clusterPosition)
    //                                let info = Some (sprintf "%s %i" fileName clusterPosition)
    //                                filterFound.Post (res, buffer, clusterPosition, info)
    //                            })
    //                        |> Async.RunSynchronously
    //                with ex ->
    //                    printfn "Error reading zip file: %s \r\n %s" fileName ex.Message
    //        finally
    //            decompressedStream.Close()
    //        )        

    // wait for the writer to finish
    storeSector.PostAndReply Complete
finally
    docSearchingAgent.Post None
    logger.Debug("Waiting for the zip scanner to finish...")
    if are.Reset() then
       zipSearchingAgent.Post None
    if are.WaitOne() then
        logger.Debug("Finished.")
        hddImage.Close()
    NLog.LogManager.Shutdown()