module Zip
    open System
    open System.IO    
    open System.IO.Compression
    let LOCAL_FILE_HEADER_SIGNATURE = 0x04034b50u
    let LOCAL_FILE_OFFSETS_FILENAME = "lh_offsets.adr"

    type ZipOffsetStorageMsg = 
        | StoreOffset of (int64 list * int64)
        | CompleteStoreOffset of AsyncReplyChannel<unit>
    let inline getBytes (i : int64) = BitConverter.GetBytes i

    /// Store offsets of local zip file headers for later processing
    let storeLocalHeaderOffset = MailboxProcessor<ZipOffsetStorageMsg>.Start(fun inbox ->
        let fileName = Path.Combine(config.output_dir, LOCAL_FILE_OFFSETS_FILENAME)
        let fileStream = File.OpenWrite(fileName)
        let collectOffsets position = List.collect ((+) position >> getBytes >> Array.toList)
        let rec loop(buffer: byte list) = async {
            match! inbox.Receive() with
            | StoreOffset (res, position) when buffer.Length > 256 -> 
                do! fileStream.WriteAsync(buffer |> Array.ofList, 0, buffer.Length) |> Async.AwaitTask
                let tail = collectOffsets position res
                return! loop(tail)
            | StoreOffset (res, position) -> 
                let tail = collectOffsets position res
                return! loop(buffer @ tail)            
            | CompleteStoreOffset rc ->
                do! fileStream.WriteAsync(buffer |> Array.ofList, 0, buffer.Length) |> Async.AwaitTask            
                do! fileStream.FlushAsync() |> Async.AwaitTask
                fileStream.Close()
                rc.Reply()
        }
        loop([])
    )

    let detectLocalFileHeader (sector: ReadOnlyMemory<byte>, offset: int64) =
        use ms = new MemoryStream(sector.ToArray())
        use br = new BinaryReader(ms)
        let indexes =
            [0L .. int64(sector.Length - 5)]
                |> List.choose (fun i ->
                        ms.Seek(i, SeekOrigin.Begin) |> ignore
                        let signature = br.ReadUInt32()
                        if signature = LOCAL_FILE_HEADER_SIGNATURE then
                            Some i
                        else None
                    )
        if indexes.Length > 0 then
            storeLocalHeaderOffset.Post(StoreOffset(indexes, offset))

    let scanForLocalFileHeader(buffer: byte [], position: int64) =
            let sectors = buffer.Length / 512
            [0..sectors - 1]
                |> List.map (fun i -> async {
                        let offset = i * 512
                        let sector = ReadOnlyMemory<byte>(buffer, offset, 512)
                        detectLocalFileHeader(sector, position + int64 offset)
                    })
                |> Async.Parallel
                |> Async.Ignore

    let fileName = Path.Combine(config.output_dir, LOCAL_FILE_OFFSETS_FILENAME)
    let identifyLocalFileHeader (reader: BinaryReader) =
        let signature = reader.ReadUInt32()
        if signature = LOCAL_FILE_HEADER_SIGNATURE then
            let versionNeededToExtract = reader.ReadUInt16()
            let generalPurposeBitFlag = reader.ReadUInt16()
            let compressionMethod = reader.ReadUInt16()
            let lastModFileTime = reader.ReadUInt16()
            let lastModFileDate = reader.ReadUInt16()
            let crc32 = reader.ReadUInt32()
            let compressedSize = reader.ReadUInt32()
            let uncompressedSize = reader.ReadUInt32()
            let fileNameLength = reader.ReadUInt16()
            let extraFieldLength = reader.ReadUInt16()
            let fileName = reader.ReadBytes(int fileNameLength)
            let extraField = reader.ReadBytes(int extraFieldLength)
            Some (versionNeededToExtract, generalPurposeBitFlag, compressionMethod, lastModFileTime, lastModFileDate, crc32, compressedSize, uncompressedSize, fileName, extraField)
        else None

    let identifyLFHPositions () =
        let positions = seq{
            use hddImage = File.OpenRead(config.input_file)
            use hddReader = new BinaryReader(hddImage)
            use file = File.OpenRead(fileName)
            use reader = new BinaryReader(file)
            while reader.BaseStream.Position < reader.BaseStream.Length do
                let position = reader.ReadInt64()
                hddImage.Seek(position, SeekOrigin.Begin) |> ignore                
                match identifyLocalFileHeader hddReader with
                | Some (versionNeededToExtract, generalPurposeBitFlag, compressionMethod, lastModFileTime, lastModFileDate, crc32, compressedSize, uncompressedSize, fileName, extraField) ->
                    yield position, fileName
                | None -> ()
        }
        positions

    let printXMLUncompressedContent (positions: (int64 * string) list) =
        use hddImage = File.OpenRead(config.input_file)
        use hddReader = new BinaryReader(hddImage)
        positions
            |> List.iter (fun (position, fn) ->
                hddImage.Seek(position, SeekOrigin.Begin) |> ignore
                match identifyLocalFileHeader hddReader with
                | Some (versionNeededToExtract, generalPurposeBitFlag, compressionMethod, lastModFileTime, lastModFileDate, crc32, compressedSize, uncompressedSize, fileName, extraField) ->
                    printfn "%s %i" fn compressedSize
                    let content = hddReader.ReadBytes(int compressedSize)
                    let ms = new MemoryStream(content)
                    try
                        let zip = new DeflateStream(ms, CompressionMode.Decompress)
                        let buffer = Array.zeroCreate<byte> (int uncompressedSize)
                        zip.Read(buffer, 0, int uncompressedSize) |> ignore
                        let content = System.Text.Encoding.UTF8.GetString(buffer)
                        File.WriteAllText(Path.Combine(config.output_dir, position.ToString()), content)
                    with exn ->
                        printfn "Error: %s" exn.Message
                | None -> ()
            )

