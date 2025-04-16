module GPU
    open IStack
    open Brahma.FSharp
    open System
    open System.Collections.Generic

    let kernel =
        <@  // returns the number of found keywords in a sector   
            // only indicates zip local file header or .doc signature is present 
            // assuming they can't share single sector
            fun (range: Range1D) 
                count (needles: ClArray<byte>) (needlesIndexes: ClArray<int>) // Store all neddles in a single array, and adress them by index
                (cluster: ClArray<byte>) (indicator: ClArray<byte>) ->
                let i = range.GlobalID0               
                let sectorOffset = i * 512
                indicator[i] <- 0uy
                let toLower (b: byte) : byte =  if b >= 65uy && b <= 90uy then b + 32uy else b
                
                for j = 0 to 504 do
                    let innerOffset = sectorOffset + j
                    // zip local file signature
                    let isLocalFileHeader =  
                        cluster.[innerOffset + 3] = 0x04uy && 
                        cluster.[innerOffset + 2] = 0x03uy && 
                        cluster.[innerOffset + 1] = 0x4buy && 
                        cluster.[innerOffset] = 0x50uy
                    // doc file signature D0CF11E0A1B11AE1
                    let isDoc = 
                        cluster.[innerOffset] = 0xD0uy && 
                        cluster.[innerOffset + 1] = 0xCFuy && 
                        cluster.[innerOffset + 2] = 0x11uy && 
                        cluster.[innerOffset + 3] = 0xE0uy && 
                        cluster.[innerOffset + 4] = 0xA1uy && 
                        cluster.[innerOffset + 5] = 0xB1uy && 
                        cluster.[innerOffset + 6] = 0x1Auy && 
                        cluster.[innerOffset + 7] = 0xE1uy    
                    // using high values for local file header and doc file signature
                    if isLocalFileHeader then
                        indicator.[i] <- 100uy
                    if isDoc then
                        indicator.[i] <- 200uy
                if indicator[i] = 0uy then
                    // search for keywords
                    for k = 0 to count - 1 do
                        let mutable matches = 0uy
                        let needleOffset = needlesIndexes.[k]
                        let needleLength = needlesIndexes.[k+1] - needlesIndexes.[k] 
                        for j = 0 to 511 - needleLength + 1 do
                            let mutable found = true
                            let innerOffset = sectorOffset + j
                            for l = 0 to needleLength - 1 do
                                found <- found && 
                                    (toLower cluster.[innerOffset + l] = toLower needles.[needleOffset + l])
                            // using low numbers for keywords
                            if found then
                                matches <- matches + 1uy
                        if matches > 0uy then
                            indicator.[i] <- indicator.[i] + 1uy
        @>

    type GPUStack(blocks, needles: IList<string>, matchAll) =
        inherit IStack(blocks, needles)
        let workGroupSize = config.work_size
        let device = ClDevice.GetFirstAppropriateDevice()
        do
            printfn "device: %s" device.Name
            printfn "max work size: %i" device.MaxWorkGroupSize
            printfn "work group size: %i" workGroupSize
        let context = ClContext(device)
        let mainQueue = context.QueueProvider.CreateQueue()
        let needlesCombined = Config.keywords @ Config.signatures
        let byteNeedles = needlesCombined |> Seq.concat |> Seq.toArray
        let needlesIndexes = 
            needlesCombined 
                |> List.map (Array.length) 
                |> List.scan (fun acc x -> acc + x) 0
                |> List.toArray
        let searchNeedles (s:string) =
            if matchAll then
                if (needles |> Seq.forall s.Contains) then needles.Count else 0
            else
                (needles |> Seq.filter s.Contains |> Seq.length)
        override _.Scan(buffer: byte [], position: int64) =
            use clByte = context.CreateClArray<byte>(buffer)

            let kernel = context.Compile kernel
            let arrayScan (context: ClContext) workGroupSize =
                fun (commandQueue: MailboxProcessor<_>) (inputArray1: ClArray<byte>) ->
                    let ndRange = Range1D.CreateValid(config.block_size, workGroupSize)
                    let outputArray = context.CreateClArray(config.block_size, allocationMode = AllocationMode.Default)
                    let needles = context.CreateClArray<byte>(byteNeedles)
                    let needlesIndexes = context.CreateClArray<int>(needlesIndexes)
                    let kernel = kernel.GetKernel()

                    commandQueue.Post(
                        Msg.MsgSetArguments
                            (fun () -> kernel.KernelFunc ndRange (Config.keywords.Length) needles needlesIndexes inputArray1 outputArray)
                    )

                    commandQueue.Post(Msg.CreateRunMsg<_, _> kernel)

                    outputArray
            let intArrayScan = arrayScan context workGroupSize
            use intRes = intArrayScan mainQueue clByte
            let resOnHost = Array.zeroCreate config.block_size
            let res = mainQueue.PostAndReply(fun ch -> Msg.CreateToHostMsg(intRes, resOnHost, ch))
            async {return res}
        override _.SearchIn (arg: string): int = searchNeedles arg        
