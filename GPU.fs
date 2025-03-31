module GPU
    open Brahma.FSharp
    let kernel_ =
        <@
            fun (range: Range1D) wordsMin wordsMax (array1: ClArray<byte>) (result: ClArray<byte>) ->
                let i = range.GlobalID0
                let mutable words = 0uy
                let mutable state = 0uy
                let mutable found = false
                let mutable EOCD = false
                let offset = i * 512

                let isZip = array1.[offset + 0] = 0x50uy && array1.[offset + 1] = 0x4Buy && array1.[offset + 2] = 0x03uy && array1.[offset + 3] = 0x04uy
                if isZip then // identify and handle zip files differently
                    result.[i] <- 2uy
                else // identify txt files
                    let letterPredicate value = value >= 65uy && value <= 122uy
                    let spacePredicate value = value = 32uy || value = 9uy 
                    let crPredicate value = value = 10uy || value = 13uy
                    let candidateFound words = words > (byte)wordsMin && words < (byte)wordsMax

                    for j = 0 to 511 do
                        let idx = offset + j
                        if j + 3 < 512 then
                            EOCD <- EOCD || (array1.[idx+3] = 0x06uy && array1.[idx + 2] = 0x05uy && array1.[idx + 1] = 0x4buy && array1.[idx] = 0x50uy)
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
                    if EOCD then
                        result.[i] <- 3uy
        @>
    let device = ClDevice.GetFirstAppropriateDevice()
    printfn "device: %s" device.Name
    printfn "max work size: %i" device.MaxWorkGroupSize
    let context = ClContext(device)
    let mainQueue = context.QueueProvider.CreateQueue()

    let arrayScan (clContext: ClContext) wordsMin wordsMax workGroupSize =
        let kernel =
            <@
                fun (range: Range1D) wordsMin wordsMax (array1: ClArray<byte>) (result: ClArray<byte>) ->
                    let i = range.GlobalID0
                    let offset = i * 512
                    let mutable zheader = false

                    for j = 0 to 511 do
                        let idx = offset + j
                        if j < 509 then
                            zheader <- zheader ||  (array1.[idx+3] = 0x04uy && array1.[idx + 2] = 0x03uy && array1.[idx + 1] = 0x4buy && array1.[idx] = 0x50uy)
                    result.[i] <- if zheader then 1uy else 0uy
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

