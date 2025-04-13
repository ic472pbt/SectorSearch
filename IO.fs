module IO
    open System
    open System.IO
    open FSharp.Control

    let cts = new System.Threading.CancellationTokenSource()
    type StorageMsg = 
        | StoreSector of (byte[] * int * int64 * byte)
        | Complete of AsyncReplyChannel<unit>
   
    let sw = System.Diagnostics.Stopwatch.StartNew()

    /// Read stream in blocks. Each block size is proportional to 512 bytes.
    let readStream fileSize blockSize (stream: Stream) = 
        let blocks = fileSize / (int64)blockSize
        printfn "blocks: %i" blocks
        asyncSeq{
            let mutable position = 0L
            let mutable len = 0L
            let readBuffer = Array.zeroCreate<byte> blockSize
            let! length = stream.ReadAsync(readBuffer, 0, blockSize) |> Async.AwaitTask
            len <- int64 length
            while len > 0 && not cts.IsCancellationRequested do
                yield readBuffer, position
                let currentBlock = position / (int64) blockSize
                System.Console.CursorLeft <- 0
                printf "position block: %i/%i (%.01f%%) speed: %.01fMb/s" 
                    currentBlock blocks ((float)currentBlock/(float)blocks*100.0)
                    ((float) position / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds)
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
            | StoreSector (bytes, start, position, cnt) ->
                // Write the bytes to the output file
                let count = Text.Encoding.ASCII.GetBytes($"{position + int64 (start * 512)} cnt {cnt.ToString()}\r\n")
                do! outputFileStream.WriteAsync(count, 0, count.Length) |> Async.AwaitTask
                do! outputFileStream.WriteAsync(bytes, start, 512) |> Async.AwaitTask
                outputFileStream.Write(crlf, 0, 2)
                outputFileStream.Write(crlf, 0, 2)
                return! loop()
        }
        loop()
    )