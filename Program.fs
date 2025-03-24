open System.IO
open Config
open FSharp.Control
open Brahma.FSharp

if not <| Directory.Exists (config.output_dir) then
    Directory.CreateDirectory(config.output_dir) |> ignore

let wordsMin = config.words_min
let wordsMax = config.words_max

let readBlockSize = config.block_size * 512
printfn "read block: %i" readBlockSize
let arrayScan (clContext: ClContext) workGroupSize =
    let kernel =
        <@
            fun (range: Range1D) wordsMin wordsMax (array1: ClArray<byte>) (result: ClArray<byte>) ->
                let i = range.GlobalID0
                let mutable words = 0uy
                let mutable state = 0uy
                let mutable found = false

                let letterPredicate value = value >= 65uy && value <= 122uy
                let spacePredicate value = value = 32uy || value = 9uy 
                let crPredicate value = value = 10uy || value = 13uy
                let candidateFound words = words > (byte)wordsMin && words < (byte)wordsMax

                for j = 0 to 511 do
                    let idx = i * 512 + j
                    let value = array1.[idx]
                    if state = 0uy then 
                        if letterPredicate value then
                            state <- 1uy // found first letter
                    elif state = 1uy then
                        if spacePredicate value then
                            state <- 2uy // found space after word
                            words <- words + 1uy
                        elif crPredicate value then
                            state <- 3uy // found end of line
                            words <- words + 1uy
                        elif letterPredicate value then
                            state <- 1uy // found next letter
                        else 
                            found <- found || candidateFound words
                            words <- 0uy
                            state <- 0uy // found non-letter character
                    elif state = 3uy || state = 2uy then
                        if crPredicate value then
                            state <- 3uy // found end of line
                        elif spacePredicate value then
                            state <- 2uy // found space after first word
                        elif letterPredicate value then
                            state <- 1uy // found next letter
                        else
                            found <- found || candidateFound words
                            words <- 0uy
                            state <- 0uy // found non-letter character

                    result.[i] <- if found || candidateFound words then 1uy else 0uy
        @>

    let kernel = clContext.Compile kernel

    fun (commandQueue: MailboxProcessor<_>) (inputArray1: ClArray<byte>) ->
        let ndRange = Range1D.CreateValid(config.block_size, workGroupSize)
        let outputArray = clContext.CreateClArray(config.block_size, allocationMode = AllocationMode.Default)

        let kernel = kernel.GetKernel()

        commandQueue.Post(
            Msg.MsgSetArguments
                (fun () -> kernel.KernelFunc ndRange wordsMin wordsMax inputArray1 outputArray)
        )

        commandQueue.Post(Msg.CreateRunMsg<_, _> kernel)

        outputArray


let fileSize = 
    use f = File.Open(config.input_file, FileMode.Open, FileAccess.Read)
    f.Length
printfn "file size: %i" fileSize
let blocks = fileSize / (int64)readBlockSize
printfn "blocks: %i" blocks

let device = ClDevice.GetFirstAppropriateDevice()
let context = ClContext(device)
let mainQueue = context.QueueProvider.CreateQueue()

let sw = System.Diagnostics.Stopwatch.StartNew()

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
        printfn "position block: %i/%i (%.01f%%) speed: %.01fMb/s" 
            currentBlock blocks ((float)currentBlock/(float)blocks*100.0)
            ((float) position / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds)
        position <- position + int64 length
        let! length = f.ReadAsync(readBuffer, 0, readBuffer.Length) |> Async.AwaitTask
        len <- length
}

let findWordsAny(bytes: byte []) =
    let s = System.Text.Encoding.ASCII.GetString(bytes)
    config.keywords |> Seq.exists s.Contains

let findWordsAll(bytes: byte []) =
    let s = System.Text.Encoding.ASCII.GetString(bytes)
    config.keywords |> Seq.forall s.Contains

let degreeOfParallelizm = System.Environment.ProcessorCount - 1

/// Do keyword search in block
let matcher() = MailboxProcessor.Start(fun inbox ->
    let findWords = if config.match_all then findWordsAll else findWordsAny
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

let workers = [for _ in 1..degreeOfParallelizm -> matcher()]

/// Balancer for workers
let balancer = MailboxProcessor<byte[] * int64>.Start(fun inbox ->
    let rec loop(workerIndex) = async {
        let! (bytes, offset) = inbox.Receive()
        workers[workerIndex].Post (bytes, offset)
        return! loop((workerIndex + 1) % degreeOfParallelizm)
    }
    loop(0)
)

readBlock
    |> AsyncSeq.iter (fun (buffer, position) ->
        printfn "position: %i" position
        use clIntA1 = context.CreateClArray<byte>(buffer)
        let intArrayScan = arrayScan context 512
        use intRes = intArrayScan mainQueue clIntA1
        let resOnHost = Array.zeroCreate config.block_size
        let res = mainQueue.PostAndReply(fun ch -> Msg.CreateToHostMsg(intRes, resOnHost, ch))
        res 
            |> Array.iteri (fun i v ->
                if v = 1uy then
                    let offset = position + (int64) i * 512L
                    let sector = Array.zeroCreate<byte> 512
                    System.Array.Copy(buffer, i * 512, sector, 0, 512)      
                    balancer.Post(sector, offset)
              )
        )
    |> Async.RunSynchronously

printfn "Elapsed: %A" sw.Elapsed