module IO
    open System.IO
    open FSharp.Control
    let readBlockSize = config.block_size * 512
    printfn "read block: %i" readBlockSize
    
    let fileSize = 
        use f = File.Open(config.input_file, FileMode.Open, FileAccess.Read)
        f.Length
    printfn "file size: %i" fileSize
    let blocks = fileSize / (int64)readBlockSize
    printfn "blocks: %i" blocks
    
    let sw = System.Diagnostics.Stopwatch.StartNew()

    /// Read file in blocks. Each block size is proportional to 512 bytes.
    let readBlock = asyncSeq{
        let mutable position = 0L
        let mutable len = 0
        let readBuffer = Array.zeroCreate<byte> readBlockSize
        use f = File.Open(config.input_file, FileMode.Open, FileAccess.Read)
        let! length = f.ReadAsync(readBuffer, 0, readBuffer.Length) |> Async.AwaitTask
        len <- length
        while len > 0 && len = readBlockSize do
            yield readBuffer, position
            let currentBlock = position / (int64) readBlockSize
            System.Console.CursorLeft <- 0
            printf "position block: %i/%i (%.01f%%) speed: %.01fMb/s" 
                currentBlock blocks ((float)currentBlock/(float)blocks*100.0)
                ((float) position / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds)
            position <- position + int64 length
            let! length = f.ReadAsync(readBuffer, 0, readBuffer.Length) |> Async.AwaitTask
            len <- length
    }
