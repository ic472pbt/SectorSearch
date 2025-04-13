module Zip
    open IStack
    open System
    open System.IO    
    open System.IO.Compression
    let LOCAL_FILE_HEADER_SIGNATURE = 0x04034b50u
    let LOCAL_FILE_OFFSETS_FILENAME = "lh_offsets.adr"

    let cts = new System.Threading.CancellationTokenSource()
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
            | StoreOffset (res, position) when buffer.Length > 1024 -> 
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

    /// Detect local file header signature in a sector
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

    /// Scan the cluster/block for local file headers
    let scanForLocalFileHeader(cluster: byte [], position: int64) =
            let sectors = cluster.Length / 512
            [0..sectors - 1]
                |> List.map (fun i -> async {
                        let offset = i * 512
                        let sector = ReadOnlyMemory<byte>(cluster, offset, 512)
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
            let filenameDecoder(b: byte array) = 
                //if generalPurposeBitFlag &&& 1024us = 1024us then
                    System.Text.Encoding.UTF8.GetString b
                //else
                //    System.Text.Encoding.ASCII.GetString b
            let fileName = 
                fileNameLength
                    |> int
                    |> reader.ReadBytes
                    |> filenameDecoder
            let extraField = reader.ReadBytes(int extraFieldLength)
            // skip damaged or ZIP64 records
            if fileName.Length < 2048 && compressedSize < UInt32.MaxValue && compressedSize > 0u then
                Some (versionNeededToExtract, generalPurposeBitFlag, compressionMethod, lastModFileTime, lastModFileDate, crc32, compressedSize, uncompressedSize, fileName, extraField)
            else
                None
        else None

    let identifyLFHPositions (hddImage: FileStream) =
        hddImage.Position <- 0L
        let positions = seq{
            use hddReader = new BinaryReader(hddImage)
            use file = File.OpenRead(fileName)
            use reader = new BinaryReader(file)
            while reader.BaseStream.Position < reader.BaseStream.Length && not cts.IsCancellationRequested do
                let position = reader.ReadInt64()
                hddImage.Seek(position, SeekOrigin.Begin) |> ignore                
                match identifyLocalFileHeader hddReader with
                | Some (versionNeededToExtract, generalPurposeBitFlag, 
                        compressionMethod, lastModFileTime, lastModFileDate, crc32, 
                        compressedSize, uncompressedSize, fileName, extraField) 
                        when ([".class"; ".png"; ".gif"; ".jpg"; ".dll"; ".exe"; ".ico"; ".jpeg"; ".ogg";
                                ".svg"; ".ttf"; ".pak"] 
                                |> List.exists fileName.EndsWith |> not) ->
                    printfn "%s %i" fileName compressedSize
                    let compressedStream = hddReader.ReadBytes(min (int64 compressedSize) (10L*1024L*1024L) |> int) // 10Mb limit
                    let ms = new MemoryStream(compressedStream)
                    let decompressedStream = new DeflateStream(ms, CompressionMode.Decompress)
                    yield position, fileName, int32 uncompressedSize, decompressedStream
                | _ -> ()
        }
        positions

    let printXMLUncompressedContent (hddImage: FileStream) (searcher: IStack) (positions: (int64 * string) list) =
        use hddReader = new BinaryReader(hddImage)
        positions
            |> List.iter (fun (position, fn) ->
                hddImage.Seek(position, SeekOrigin.Begin) |> ignore
                match identifyLocalFileHeader hddReader with
                | Some (versionNeededToExtract, generalPurposeBitFlag, compressionMethod, lastModFileTime, lastModFileDate, crc32, compressedSize, uncompressedSize, fileName, extraField) ->
                    printfn "%s %i" fn compressedSize
                    let content = hddReader.ReadBytes(int compressedSize)
                    use ms = new MemoryStream(content)
                    try
                        let content = 
                            use zip = new DeflateStream(ms, CompressionMode.Decompress)
                            let buffer = Array.zeroCreate<byte> (int uncompressedSize)
                            zip.Read(buffer, 0, int uncompressedSize) |> ignore
                            System.Text.Encoding.UTF8.GetString(buffer)
                        if searcher.SearchIn content > 0 then
                            File.WriteAllText(Path.Combine(config.output_dir, "zip", position.ToString() + " " + Path.GetFileName fileName), content)
                    with exn ->
                        printfn "Error: %s" exn.Message
                | None -> ()
            )

