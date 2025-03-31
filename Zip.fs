module Zip
    open System.IO    
    open System.IO.Compression
    let fileName = Path.Combine(config.output_dir, $"offsets.adr")
    let identifyLocalFileHeader (reader: BinaryReader) =
        let signature = reader.ReadUInt32()
        if signature = 0x04034b50u then
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

