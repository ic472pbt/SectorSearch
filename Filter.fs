module Filter
    open IO
    /// Select sectors containing keywords
    let filterFound = MailboxProcessor<byte[] * byte[] * int64 * string option>.Start(fun inbox ->
        let wordsLimit = (byte)config.min_words
        let rec loop() = async {
            let! (res, buffer, clusterPosition, info) = inbox.Receive()
            res |> Array.iteri (fun i wordsCount ->
                if wordsCount >= wordsLimit && wordsCount < 100uy then
                    let sectorPosition = i * 512
                    storeSector.Post <| StoreSector(buffer, sectorPosition, clusterPosition, wordsCount, info)
            )
            return! loop()
        }
        loop()
    )
