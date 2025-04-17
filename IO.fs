module IO
    open System
    open System.IO
    open FSharp.Control

    let cts = new System.Threading.CancellationTokenSource()
    type StorageMsg = 
        | StoreSector of (byte[] * int * int64 * byte * string option)
        | Complete of AsyncReplyChannel<unit>
   

    /// Read stream in blocks. Each block size is proportional to 512 bytes.
    let readStream fileSize blockSize (stream: Stream) = 
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let blocks = fileSize / (int64)blockSize
        printfn "blocks: %i" blocks
        asyncSeq{
            let mutable prevPosition = 0L
            let mutable position = 0L
            let slidingSpeed = 0.0
            let mutable curMillis = sw.ElapsedMilliseconds
            let mutable prevMillis = 0L
            let mutable len = 0L
            let readBuffer = Array.zeroCreate<byte> blockSize
            let! length = stream.ReadAsync(readBuffer, 0, blockSize) |> Async.AwaitTask
            len <- int64 length
            while len > 0 && not cts.IsCancellationRequested do
                yield readBuffer, position
                let currentBlock = position / (int64) blockSize
                System.Console.CursorLeft <- 0
                curMillis <- sw.ElapsedMilliseconds
                let curSpeed = float(position - prevPosition)/ 1.024 / 1024.0 / (float(curMillis - prevMillis))
                printf "position block: %i/%i (%.01f%%) speed: %.01fMb/s ETA: %A Elapsed: %A" 
                    currentBlock blocks ((float)currentBlock/(float)blocks*100.0)
                    curSpeed
                    (TimeSpan.FromMilliseconds((float)(blocks - currentBlock) * (float)(curMillis - prevMillis)))
                    sw.Elapsed
                prevMillis <- curMillis
                prevPosition <- position
                position <- position + len
                let! length = stream.ReadAsync(readBuffer, 0, blockSize) |> Async.AwaitTask
                len <- int64 length
        }    
        
    let storeSector = MailboxProcessor<StorageMsg>.Start(fun inbox ->
        let crlf = System.Text.Encoding.ASCII.GetBytes("\r\n")
        let outputFile = Path.Combine(config.output_dir, "sectors_txt.bin")
        let outputFileStream = File.Open(outputFile, FileMode.Create, FileAccess.Write)
        let rec loop() = async {
            let! msg = inbox.Receive()
            match msg with
            | Complete rc -> 
                do! outputFileStream.FlushAsync() |> Async.AwaitTask
                outputFileStream.Close()
                rc.Reply()
                return ()
            | StoreSector (bytes, sectorPosition, clusterPosition, cnt, info) ->
                let info = info |> Option.defaultValue ""
                // Write the bytes to the output file
                let count = Text.Encoding.ASCII.GetBytes($"{clusterPosition + (int64 sectorPosition)} cnt {cnt.ToString()} {info}\r\n")
                do! outputFileStream.WriteAsync(count, 0, count.Length) |> Async.AwaitTask
                do! outputFileStream.WriteAsync(bytes, sectorPosition, 512) |> Async.AwaitTask
                outputFileStream.Write(crlf, 0, 2)
                outputFileStream.Write(crlf, 0, 2)
                return! loop()
        }
        loop()
    )